using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.WinPak;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Sync;

public record SyncResult(int Added, int Updated, int Deactivated)
{
    public override string ToString() => $"přidáno {Added}, aktualizováno {Updated}, deaktivováno {Deactivated}";
}

/// <summary>Import čteček z WIN-PAK konektoru. Ručně vytvořené záznamy se nikdy nemažou.</summary>
public class ReaderSyncService(AcsDbContext db, WinPakClient winPak, AuditService audit)
{
    public async Task<SyncResult> SyncAsync(string? userName, CancellationToken ct = default)
    {
        var remote = await winPak.GetReadersAsync(ct);
        var existing = await db.Readers
            .Where(r => r.ExternalId != null)
            .ToDictionaryAsync(r => r.ExternalId!, ct);

        int added = 0, updated = 0, deactivated = 0;
        var now = DateTime.UtcNow;

        foreach (var r in remote)
        {
            if (existing.TryGetValue(r.Id, out var local))
            {
                local.Name = r.Name;
                local.Description = r.Description;
                local.PanelName = r.PanelName;
                local.AccountName = r.AccountName;
                local.IsActive = r.IsActive;
                local.LastSyncedAt = now;
                updated++;
            }
            else
            {
                db.Readers.Add(new Reader
                {
                    ExternalId = r.Id,
                    Name = r.Name,
                    Description = r.Description,
                    PanelName = r.PanelName,
                    AccountName = r.AccountName,
                    IsActive = r.IsActive,
                    Source = RecordSource.Imported,
                    LastSyncedAt = now,
                });
                added++;
            }
        }

        // Importované čtečky, které z WIN-PAK zmizely, jen deaktivujeme (kvůli historii žádostí).
        var remoteIds = remote.Select(r => r.Id).ToHashSet();
        foreach (var (externalId, local) in existing)
        {
            if (!remoteIds.Contains(externalId) && local.Source == RecordSource.Imported && local.IsActive)
            {
                local.IsActive = false;
                deactivated++;
            }
        }

        await db.SaveChangesAsync(ct);
        var result = new SyncResult(added, updated, deactivated);
        await audit.LogAsync(userName, "readers-synced", "Reader", null, result.ToString(), ct);
        return result;
    }
}
