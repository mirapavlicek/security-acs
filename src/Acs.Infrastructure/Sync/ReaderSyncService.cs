using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.WinPak;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Sync;

public record SyncResult(int Added, int Updated, int Deactivated, int Linked = 0, int Merged = 0)
{
    public override string ToString()
    {
        var text = $"přidáno {Added}, aktualizováno {Updated}, deaktivováno {Deactivated}";
        if (Linked > 0)
            text += $", spárováno s čtečkami z dokumentace podle čísla: {Linked}";
        if (Merged > 0)
            text += $", sloučeno duplicit: {Merged}";
        return text;
    }
}

/// <summary>
/// Import čteček z WIN-PAK konektoru. Ručně vytvořené záznamy se nikdy nemažou.
///
/// Čtečky přicházejí ze dvou stran: z WIN-PAKu (id zařízení, název = číslo čtečky,
/// panel) a z dokumentace EKV (číslo čtečky, jméno místnosti, umístění v plánu).
/// Je to jedna fyzická čtečka a v ACS má být jeden záznam: WIN-PAK dodá id pro
/// zápis přístupů, dokumentace umístění. Páruje se přes <b>číslo čtečky</b> —
/// název zařízení ve WIN-PAKu je totéž číslo jako <see cref="Reader.DeviceNumber"/>
/// z EKV. U spárované čtečky se z WIN-PAKu bere jen id, účet a aktivita; jméno,
/// rozvaděč a umístění z dokumentace zůstávají.
/// </summary>
public class ReaderSyncService(AcsDbContext db, WinPakClient winPak, AuditService audit, ReaderCleanupService cleanup)
{
    public async Task<SyncResult> SyncAsync(string? userName, CancellationToken ct = default)
    {
        var remote = await winPak.GetReadersAsync(ct);
        var all = await db.Readers.ToListAsync(ct);
        var existing = all.Where(r => r.ExternalId != null).ToDictionary(r => r.ExternalId!);
        var byNumber = all.Where(r => r.ExternalId == null && r.DeviceNumber != null)
            .GroupBy(r => Normalize(r.DeviceNumber!))
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First());

        int added = 0, updated = 0, deactivated = 0, linked = 0;
        var now = DateTime.UtcNow;

        foreach (var r in remote)
        {
            if (existing.TryGetValue(r.Id, out var local))
            {
                Refresh(local, r, now);
                updated++;
            }
            else if (byNumber.Remove(Normalize(r.Name), out var documented))
            {
                // Čtečka z dokumentace bez id WIN-PAKu: WIN-PAK ji zná pod stejným číslem.
                documented.ExternalId = r.Id;
                Refresh(documented, r, now);
                existing[r.Id] = documented;
                linked++;
            }
            else
            {
                var reader = new Reader
                {
                    ExternalId = r.Id,
                    Name = r.Name,
                    Description = r.Description,
                    PanelName = r.PanelName,
                    AccountName = r.AccountName,
                    IsActive = r.IsActive,
                    Source = RecordSource.Imported,
                    LastSyncedAt = now,
                };
                db.Readers.Add(reader);
                existing[r.Id] = reader;
                added++;
            }
        }

        // Importované čtečky, které z WIN-PAK zmizely, jen deaktivujeme (kvůli historii žádostí).
        var remoteIds = remote.Select(r => r.Id).ToHashSet();
        foreach (var (externalId, local) in existing)
        {
            if (!remoteIds.Contains(externalId) && local.Source == RecordSource.Imported && local.IsActive && local.DeviceNumber is null)
            {
                local.IsActive = false;
                deactivated++;
            }
        }

        await db.SaveChangesAsync(ct);
        var merged = await MergeDuplicatesAsync(userName, ct);

        var result = new SyncResult(added, updated, deactivated, linked, merged);
        await audit.LogAsync(userName, "readers-synced", "Reader", null, result.ToString(), ct);
        return result;
    }

    /// <summary>Z WIN-PAKu se u čtečky z dokumentace nepřepisuje jméno ani rozvaděč — ty má lepší dokumentace.</summary>
    private static void Refresh(Reader local, WinPakReader remote, DateTime now)
    {
        if (local.DeviceNumber is null)
        {
            local.Name = remote.Name;
            local.Description = remote.Description;
            local.PanelName = remote.PanelName;
        }

        local.AccountName = remote.AccountName;
        local.IsActive = remote.IsActive;
        local.LastSyncedAt = now;
    }

    /// <summary>
    /// Duplicity z dřívějších synchronizací: čtečka založená z WIN-PAKu (jen číslo, bez
    /// umístění) vedle čtečky z dokumentace se stejným číslem bez id. Id, účet
    /// a mapování úrovně se přenesou na dokumentovanou čtečku; duplicita se smaže,
    /// nebo — když na ní visí žádost či skupina — deaktivuje.
    /// </summary>
    private async Task<int> MergeDuplicatesAsync(string? userName, CancellationToken ct)
    {
        var all = await db.Readers.ToListAsync(ct);
        var documented = all.Where(r => r.ExternalId == null && r.DeviceNumber != null)
            .GroupBy(r => Normalize(r.DeviceNumber!))
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First());
        if (documented.Count == 0)
            return 0;

        var duplicates = new List<Reader>();
        foreach (var synced in all.Where(r => r.ExternalId != null && r.DeviceNumber == null && r.RoomId == null && r.CorridorId == null))
        {
            if (!documented.Remove(Normalize(synced.Name), out var target))
                continue;

            target.ExternalId = synced.ExternalId;
            target.AccountName = synced.AccountName;
            target.AccessLevelExternalId ??= synced.AccessLevelExternalId;
            target.IsActive = synced.IsActive;
            target.LastSyncedAt = synced.LastSyncedAt;

            synced.ExternalId = null;
            duplicates.Add(synced);
        }

        if (duplicates.Count == 0)
            return 0;

        await db.SaveChangesAsync(ct);
        await cleanup.RemoveAsync(duplicates.Select(d => d.Id).ToList(), userName, ct);
        return duplicates.Count;
    }

    /// <summary>Číslo čtečky bez mezer a úvodních nul — „330011“ a „ 0330011 “ je totéž.</summary>
    internal static string Normalize(string value)
    {
        var trimmed = value.Trim();
        var digits = trimmed.All(char.IsDigit) ? trimmed.TrimStart('0') : trimmed;
        return (digits.Length == 0 ? "0" : digits).ToUpperInvariant();
    }
}
