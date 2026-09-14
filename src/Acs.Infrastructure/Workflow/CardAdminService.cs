using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Notifications;
using Acs.Infrastructure.WinPak;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Workflow;

/// <summary>
/// Fronta správce karet: schválené položky se předávají do WIN-PAK buď
/// voláním API (přes konektor), nebo je správce zadá ručně a v ACS potvrdí.
/// Skupinová položka se předává jako všechny čtečky skupiny (rekurzivně).
/// </summary>
public class CardAdminService(AcsDbContext db, WinPakClient winPak, AuditService audit,
    INotificationService? notifier = null, ReaderGroupService? groups = null)
{
    private ReaderGroupService Groups => groups ?? new ReaderGroupService(db);

    /// <summary>Schválené položky čekající na zadání do WIN-PAK (včetně karet zaměstnance — může jich mít víc).</summary>
    public Task<List<AccessRequestItem>> GetQueueAsync(CancellationToken ct = default)
        => db.AccessRequestItems
            .Include(i => i.Request!).ThenInclude(r => r.TargetEmployee!).ThenInclude(e => e.Identifiers)
            .Include(i => i.Request!).ThenInclude(r => r.RequesterUser)
            .Include(i => i.Reader)
            .Include(i => i.ReaderGroup)
            .Where(i => i.Status == RequestStatus.Approved && i.ParkingPermitId == null && i.SecurityRequestId == null)
            .OrderBy(i => i.Request!.CreatedAt)
            .ToListAsync(ct);

    /// <summary>Access levely, které položka pokrývá (čtečka, nebo celá skupina rekurzivně).</summary>
    private async Task<List<string>> ResolveAccessLevelsAsync(AccessRequestItem item, CancellationToken ct)
    {
        if (item.ReaderId is not null)
        {
            return item.Reader!.AccessLevelExternalId is { } al
                ? [al]
                : throw new InvalidOperationException(
                    $"Čtečka {item.Reader.Name} nemá namapovaný WIN-PAK access level (číselník čteček).");
        }

        var readerIds = await Groups.ExpandReaderIdsAsync(item.ReaderGroupId!.Value, ct);
        var levels = await db.Readers
            .Where(r => readerIds.Contains(r.Id) && r.AccessLevelExternalId != null)
            .Select(r => r.AccessLevelExternalId!)
            .Distinct()
            .ToListAsync(ct);
        if (levels.Count == 0)
            throw new InvalidOperationException(
                $"Skupina {item.ReaderGroup?.Name} neobsahuje žádnou čtečku s namapovaným WIN-PAK access levelem.");
        return levels;
    }

    /// <summary>Předá položku do WIN-PAK přes API konektoru.</summary>
    public async Task PushAsync(int itemId, string? userName, CancellationToken ct = default)
    {
        var item = await LoadItemAsync(itemId, ct);

        var cardHolderId = item.Request!.TargetEmployee!.WinPakCardHolderId
            ?? throw new InvalidOperationException(
                $"Zaměstnanec {item.Request.TargetEmployee.FullName} nemá vyplněné WIN-PAK card holder id (číselník zaměstnanců).");
        var accessLevels = await ResolveAccessLevelsAsync(item, ct);

        if (item.Request.Kind == RequestKind.Grant)
        {
            // Oprávnění nese ve WIN-PAKu karta; konektor ho zapíše na všechny karty držitele.
            foreach (var al in accessLevels)
                await winPak.AssignAccessLevelAsync(cardHolderId, al, ct);
            item.Status = RequestStatus.PushedToWinPak;
            item.PushResult = $"API: přiřazeny access levely {string.Join(", ", accessLevels)}"
                + await DescribeCardCoverageAsync(item.Request.TargetEmployeeId, cardHolderId, ct);
        }
        else
        {
            foreach (var al in accessLevels)
                await winPak.RevokeAccessLevelAsync(cardHolderId, al, ct);
            item.Status = RequestStatus.Revoked;
            item.PushResult = $"API: odebrány access levely {string.Join(", ", accessLevels)}";
            await RevokeOriginalGrantsAsync(item, ct);
        }

        item.PushedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(userName, "item-pushed-to-winpak", "AccessRequestItem", item.Id.ToString(),
            item.PushResult, ct);
        if (notifier is not null)
            await notifier.NotifyDecidedAsync(item.Id, ct);
    }

    /// <summary>Správce karet zadal přístup do WIN-PAK ručně a potvrzuje to.</summary>
    public async Task ConfirmManualAsync(int itemId, string? userName, CancellationToken ct = default)
    {
        var item = await LoadItemAsync(itemId, ct);

        if (item.Request!.Kind == RequestKind.Grant)
        {
            item.Status = RequestStatus.ManuallyConfirmed;
            item.PushResult = "zadáno ručně";
        }
        else
        {
            item.Status = RequestStatus.Revoked;
            item.PushResult = "odebráno ručně";
            await RevokeOriginalGrantsAsync(item, ct);
        }

        item.PushedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(userName, "item-confirmed-manually", "AccessRequestItem", item.Id.ToString(), null, ct);
        if (notifier is not null)
            await notifier.NotifyDecidedAsync(item.Id, ct);
    }

    /// <summary>
    /// Člověk může mít víc karet a přístup má platit na všech. Po zápisu se proto u každé
    /// platné karty z ACS ověří, že ji WIN-PAK zná a patří tomuto držiteli (jen na takové
    /// konektor přístup zapsal); ostatní se vypíší, aby správce karet věděl, co dořešit.
    /// Ověření je informativní — když konektor karty neumí vrátit, zápis tím neselže.
    /// </summary>
    private async Task<string> DescribeCardCoverageAsync(int employeeId, string cardHolderId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var cards = (await db.EmployeeIdentifiers
                .Where(i => i.EmployeeId == employeeId && i.Type == IdentifierType.Card && i.IsActive)
                .OrderBy(i => i.Id)
                .ToListAsync(ct))
            .Where(c => c.IsValidAt(now))
            .Select(c => c.Value)
            .Distinct()
            .ToList();
        if (cards.Count == 0)
            return "";

        List<string> covered = [], missing = [], foreign = [];
        foreach (var card in cards)
        {
            try
            {
                var inWinPak = await winPak.GetCardAsync(card, ct);
                if (inWinPak is null)
                    missing.Add(card);
                else if (inWinPak.CardHolderId is { Length: > 0 } owner && owner != cardHolderId)
                    foreign.Add($"{card} (držitel {owner})");
                else
                    covered.Add(card);
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
            {
                return $"; karty z ACS: {string.Join(", ", cards)} (ověření ve WIN-PAK se nezdařilo)";
            }
        }

        var parts = new List<string>();
        if (covered.Count > 0)
            parts.Add($"přístup na kartách {string.Join(", ", covered)}");
        if (missing.Count > 0)
            parts.Add($"ve WIN-PAK chybí karty {string.Join(", ", missing)}");
        if (foreign.Count > 0)
            parts.Add($"jinému držiteli patří karty {string.Join(", ", foreign)}");
        return "; " + string.Join("; ", parts);
    }

    private async Task<AccessRequestItem> LoadItemAsync(int itemId, CancellationToken ct)
    {
        var item = await db.AccessRequestItems
            .Include(i => i.Request!).ThenInclude(r => r.TargetEmployee)
            .Include(i => i.Reader)
            .Include(i => i.ReaderGroup)
            .FirstOrDefaultAsync(i => i.Id == itemId, ct)
            ?? throw new KeyNotFoundException("Položka nenalezena.");

        if (item.Status != RequestStatus.Approved)
            throw new InvalidOperationException("Položka není ve stavu „schváleno“.");
        if (item.ParkingPermitId is not null)
            throw new InvalidOperationException("Parkovací povolení vydává správce parkování, ne fronta karet.");
        return item;
    }

    /// <summary>Po provedené revokaci označí původní udělené přístupy jako odebrané.</summary>
    private async Task RevokeOriginalGrantsAsync(AccessRequestItem revokeItem, CancellationToken ct)
    {
        var grants = await db.AccessRequestItems
            .Where(i => i.Id != revokeItem.Id
                        && (revokeItem.ReaderId != null
                            ? i.ReaderId == revokeItem.ReaderId
                            : i.ReaderGroupId == revokeItem.ReaderGroupId)
                        && i.Request!.TargetEmployeeId == revokeItem.Request!.TargetEmployeeId
                        && i.Request.Kind == RequestKind.Grant
                        && (i.Status == RequestStatus.PushedToWinPak
                            || i.Status == RequestStatus.ManuallyConfirmed))
            .ToListAsync(ct);
        foreach (var grant in grants)
            grant.Status = RequestStatus.Revoked;
    }
}
