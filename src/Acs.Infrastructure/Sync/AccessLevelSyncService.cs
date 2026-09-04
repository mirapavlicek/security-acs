using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.WinPak;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Sync;

public record AccessLevelSyncResult(int Added, int Updated, int Deactivated, int ReadersMapped, int TreesFailed)
{
    public override string ToString()
    {
        var text = $"přidáno {Added}, aktualizováno {Updated}, deaktivováno {Deactivated}";
        if (ReadersMapped > 0)
            text += $", čtečkám doplněno mapování: {ReadersMapped}";
        if (TreesFailed > 0)
            text += $", strom přístupů se nepodařilo načíst u {TreesFailed}";
        return text;
    }
}

/// <summary>
/// Zrcadlo přístupových úrovní WIN-PAKu v ACS. Seznam úrovní je jedno volání,
/// složení (čtečky a časové zóny) jedno volání na úroveň — proto se strom čte
/// jen u úrovní, které se změnily nebo ho ještě nemají, případně na vyžádání.
///
/// Vedlejší efekt s velkým dosahem: úroveň s jedinou čtečkou je „úroveň té čtečky“
/// — ACS ji potřebuje znát jako <see cref="Reader.AccessLevelExternalId"/>, aby
/// mohla čtečku přidělovat. Dosud se mapování psalo ručně; tady se doplní samo,
/// kde je jednoznačné a kde ještě chybí.
/// </summary>
public class AccessLevelSyncService(AcsDbContext db, WinPakClient winPak, AuditService audit)
{
    public async Task<AccessLevelSyncResult> SyncAsync(string? userName, bool refreshTrees = false, CancellationToken ct = default)
    {
        var remote = await winPak.GetAccessLevelsAsync(ct);
        var existing = await db.AccessLevels.Include(a => a.Entries).ToDictionaryAsync(a => a.ExternalId, ct);

        int added = 0, updated = 0, deactivated = 0, treesFailed = 0;
        var now = DateTime.UtcNow;
        var toRefresh = new List<AccessLevel>();

        foreach (var level in remote)
        {
            if (existing.TryGetValue(level.Id, out var local))
            {
                var changed = local.Name != level.Name || local.Description != level.Description || !local.IsActive;
                local.Name = level.Name;
                local.Description = level.Description;
                local.IsActive = true;
                local.LastSyncedAt = now;
                if (changed || refreshTrees || local.AccessTree is null)
                    toRefresh.Add(local);
                updated++;
            }
            else
            {
                local = new AccessLevel
                {
                    ExternalId = level.Id,
                    Name = level.Name,
                    Description = level.Description,
                    LastSyncedAt = now,
                };
                db.AccessLevels.Add(local);
                toRefresh.Add(local);
                added++;
            }
        }

        var remoteIds = remote.Select(l => l.Id).ToHashSet();
        foreach (var (externalId, local) in existing)
        {
            if (!remoteIds.Contains(externalId) && local.IsActive)
            {
                local.IsActive = false;
                deactivated++;
            }
        }

        // Seznam úrovní je v zrcadle hned; složení (jedno volání na úroveň, u 55 úrovní
        // i minuty) se doplňuje postupně a ukládá po každé úrovni — přerušení nic neztratí.
        await db.SaveChangesAsync(ct);

        foreach (var level in toRefresh)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await RefreshTreeAsync(level, ct);
                await db.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Strom je doplněk; úroveň sama je v zrcadle i bez něj.
                treesFailed++;
            }
        }

        var mapped = await MapSingleReaderLevelsAsync(ct);

        var result = new AccessLevelSyncResult(added, updated, deactivated, mapped, treesFailed);
        await audit.LogAsync(userName, "access-levels-synced", "AccessLevel", null, result.ToString(), ct);
        return result;
    }

    /// <summary>Znovu načte strom jedné úrovně (po zápisu z ACS).</summary>
    public async Task RefreshAsync(AccessLevel level, CancellationToken ct = default)
    {
        await RefreshTreeAsync(level, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task RefreshTreeAsync(AccessLevel level, CancellationToken ct)
    {
        var tree = await winPak.GetAccessTreeAsync(level.Name, ct);
        if (string.IsNullOrWhiteSpace(tree))
            return; // WIN-PAK strom nevrátil — položky (třeba právě zapsané z ACS) zůstávají

        level.AccessTree = tree;
        var parsed = AccessTreeParser.Parse(tree);
        if (parsed is null)
            return; // strom je, ale není to XML, kterému rozumíme — položky nechat, surový strom je uložený

        level.Entries.Clear();
        foreach (var entry in parsed)
            level.Entries.Add(entry);
    }

    /// <summary>
    /// Čtečkám bez mapování doplní úroveň, která obsahuje právě tu jednu čtečku.
    /// Jen aktivní úrovně a jen jednoznačné případy — když má čtečka takových úrovní víc,
    /// nechá se rozhodnutí správci.
    /// </summary>
    private async Task<int> MapSingleReaderLevelsAsync(CancellationToken ct)
    {
        var singles = await db.AccessLevels
            .Where(a => a.IsActive && a.Entries.Count == 1 && a.Entries.All(e => e.ReaderExternalId != null))
            .Select(a => new { a.ExternalId, ReaderExternalId = a.Entries.First().ReaderExternalId! })
            .ToListAsync(ct);

        var byReader = singles.GroupBy(s => s.ReaderExternalId)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().ExternalId);
        if (byReader.Count == 0)
            return 0;

        var readerIds = byReader.Keys.ToList();
        var readers = await db.Readers
            .Where(r => r.ExternalId != null && readerIds.Contains(r.ExternalId) && r.AccessLevelExternalId == null)
            .ToListAsync(ct);

        foreach (var reader in readers)
            reader.AccessLevelExternalId = byReader[reader.ExternalId!];

        if (readers.Count > 0)
            await db.SaveChangesAsync(ct);
        return readers.Count;
    }
}
