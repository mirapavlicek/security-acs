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

    /// <summary>Schválené položky čekající na zadání do WIN-PAK.</summary>
    public Task<List<AccessRequestItem>> GetQueueAsync(CancellationToken ct = default)
        => db.AccessRequestItems
            .Include(i => i.Request!).ThenInclude(r => r.TargetEmployee)
            .Include(i => i.Request!).ThenInclude(r => r.RequesterUser)
            .Include(i => i.Reader)
            .Include(i => i.ReaderGroup)
            .Where(i => i.Status == RequestStatus.Approved)
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
            foreach (var al in accessLevels)
                await winPak.AssignAccessLevelAsync(cardHolderId, al, ct);
            item.Status = RequestStatus.PushedToWinPak;
            item.PushResult = $"API: přiřazeny access levely {string.Join(", ", accessLevels)}";
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
