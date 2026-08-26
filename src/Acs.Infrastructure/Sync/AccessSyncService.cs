using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Auth;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.WinPak;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Sync;

public record AccessSyncResult(int ExternallyGranted, int ExternallyRevoked, int AutoConfirmed, int CardsUpdated)
{
    public override string ToString()
        => $"externě uděleno {ExternallyGranted}, externě odebráno {ExternallyRevoked}, "
         + $"automaticky potvrzeno {AutoConfirmed}, aktualizováno karet {CardsUpdated}";
}

/// <summary>
/// Zpětná synchronizace stavu z WIN-PAK do ACS. Změny provedené přímo
/// ve WIN-PAK (operátorem, jiným systémem) se propíší do ACS:
/// <list type="bullet">
///   <item><b>Externí udělení</b> — držitel má ve WIN-PAK access level, který v ACS
///     nemá aktivní: založí se systémová žádost se stavem „potvrzeno“, aby přehledy
///     a reporty odpovídaly realitě.</item>
///   <item><b>Externí odebrání</b> — ACS eviduje aktivní přístup, který už ve WIN-PAK
///     není: položka se označí jako odebraná.</item>
///   <item><b>Automatické potvrzení</b> — položka čeká ve frontě správce karet
///     a přístup už ve WIN-PAK existuje (správce ho zadal rovnou tam): položka se
///     uzavře jako ručně potvrzená.</item>
///   <item><b>Karty</b> — aktualizace čísla karty zaměstnance podle WIN-PAK.</item>
/// </list>
/// Párování: zaměstnanec ↔ držitel přes WinPakCardHolderId (fallback číslo karty);
/// čtečka ↔ access level přes Reader.AccessLevelExternalId (jeden access level
/// může pokrývat více čteček).
/// </summary>
public class AccessSyncService(AcsDbContext db, WinPakClient winPak, AuditService audit)
{
    public const string SystemUserName = "winpak-sync";

    public async Task<AccessSyncResult> SyncAsync(string? userName, CancellationToken ct = default)
    {
        var holders = await winPak.GetCardHoldersAsync(ct);
        var holderById = holders.ToDictionary(h => h.Id);
        var holderByCard = holders
            .SelectMany(h => h.Cards.Select(c => (c.CardNumber, Holder: h)))
            .GroupBy(x => x.CardNumber)
            .ToDictionary(g => g.Key, g => g.First().Holder);

        var employees = await db.Employees
            .Where(e => e.WinPakCardHolderId != null || e.CardNumber != null)
            .ToListAsync(ct);

        // Mapa access level → čtečky (jeden access level může otvírat více čteček).
        var readersByAccessLevel = (await db.Readers
                .Where(r => r.AccessLevelExternalId != null)
                .ToListAsync(ct))
            .GroupBy(r => r.AccessLevelExternalId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        var systemUser = await GetOrCreateSystemUserAsync(ct);
        int granted = 0, revoked = 0, confirmed = 0, cardsUpdated = 0;

        foreach (var employee in employees)
        {
            WinPakCardHolder? holder = null;
            if (employee.WinPakCardHolderId is not null)
                holderById.TryGetValue(employee.WinPakCardHolderId, out holder);
            if (holder is null && employee.CardNumber is not null
                && holderByCard.TryGetValue(employee.CardNumber, out var byCard))
            {
                holder = byCard;
                employee.WinPakCardHolderId ??= holder.Id; // dopárování
            }

            if (holder is null)
                continue;

            // Aktualizace čísla karty podle WIN-PAK (bere se aktivní karta).
            var activeCard = holder.Cards.FirstOrDefault(c =>
                string.Equals(c.Status, "Active", StringComparison.OrdinalIgnoreCase))
                ?? holder.Cards.FirstOrDefault();
            if (activeCard is not null && employee.CardNumber != activeCard.CardNumber)
            {
                employee.CardNumber = activeCard.CardNumber;
                cardsUpdated++;
            }

            // Čtečky, které má zaměstnanec skutečně ve WIN-PAK.
            var winPakReaderIds = holder.AccessLevelIds
                .Where(readersByAccessLevel.ContainsKey)
                .SelectMany(al => readersByAccessLevel[al])
                .Select(r => r.Id)
                .ToHashSet();

            var items = await db.AccessRequestItems
                .Include(i => i.Request)
                .Where(i => i.Request!.TargetEmployeeId == employee.Id
                            && i.Request.Kind == RequestKind.Grant)
                .ToListAsync(ct);

            var activeReaderIds = items
                .Where(i => i.Status is RequestStatus.PushedToWinPak or RequestStatus.ManuallyConfirmed)
                .Select(i => i.ReaderId)
                .ToHashSet();

            // 1) Automatické potvrzení položek z fronty, které už ve WIN-PAK jsou.
            foreach (var item in items.Where(i => i.Status == RequestStatus.Approved
                                                  && winPakReaderIds.Contains(i.ReaderId)))
            {
                item.Status = RequestStatus.ManuallyConfirmed;
                item.PushedAt = DateTime.UtcNow;
                item.PushResult = "zjištěno synchronizací — zadáno přímo ve WIN-PAK";
                activeReaderIds.Add(item.ReaderId);
                confirmed++;
            }

            // 2) Externí odebrání: aktivní v ACS, ale už ne ve WIN-PAK.
            foreach (var item in items.Where(i =>
                         (i.Status is RequestStatus.PushedToWinPak or RequestStatus.ManuallyConfirmed)
                         && !winPakReaderIds.Contains(i.ReaderId)))
            {
                item.Status = RequestStatus.Revoked;
                item.PushResult = "odebráno přímo ve WIN-PAK (zjištěno synchronizací)";
                revoked++;
            }

            // 3) Externí udělení: ve WIN-PAK je, v ACS chybí.
            var missing = winPakReaderIds.Except(activeReaderIds).ToList();
            if (missing.Count > 0)
            {
                var request = new AccessRequest
                {
                    Kind = RequestKind.Grant,
                    RequesterUserId = systemUser.Id,
                    TargetEmployeeId = employee.Id,
                    Justification = "import stavu z WIN-PAK (uděleno mimo ACS)",
                    Items = missing.Select(readerId => new AccessRequestItem
                    {
                        ReaderId = readerId,
                        Status = RequestStatus.ManuallyConfirmed,
                        PushedAt = DateTime.UtcNow,
                        PushResult = "zjištěno synchronizací — uděleno přímo ve WIN-PAK",
                    }).ToList(),
                };
                db.AccessRequests.Add(request);
                granted += missing.Count;
            }
        }

        await db.SaveChangesAsync(ct);
        var result = new AccessSyncResult(granted, revoked, confirmed, cardsUpdated);
        await audit.LogAsync(userName, "access-state-synced", "AccessRequestItem", null, result.ToString(), ct);
        return result;
    }

    /// <summary>Systémový účet, pod kterým se evidují externí změny (nelze se jím přihlásit).</summary>
    private async Task<AppUser> GetOrCreateSystemUserAsync(CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == SystemUserName, ct);
        if (user is null)
        {
            user = new AppUser
            {
                UserName = SystemUserName,
                DisplayName = "Synchronizace WIN-PAK",
                IsLocal = true,
                PasswordHash = PasswordHasher.Hash(Guid.NewGuid().ToString("N")),
                IsActive = false, // přihlášení zablokováno
                Roles = AppRole.None,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
        }

        return user;
    }
}
