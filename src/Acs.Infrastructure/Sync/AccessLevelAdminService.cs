using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.WinPak;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Sync;

/// <summary>Jedna čtečka v zadání úrovně: čtečka ACS a časová zóna WIN-PAKu, ve které na ní úroveň platí.</summary>
public record AccessLevelReaderAssignment(int ReaderId, string TimeZoneExternalId);

/// <summary>
/// Správa přístupových úrovní WIN-PAKu z ACS: založení, úprava složení, zrušení.
/// Zápis jde přes konektor jedním voláním s úplnou definicí (WIN-PAK přepíše
/// celé složení), po něm se obnoví zrcadlo. Čtečky se zadávají jako čtečky ACS
/// a překládají na id WIN-PAKu; čtečka bez id (ručně založená, nesynchronizovaná)
/// do úrovně jít nemůže.
/// </summary>
public class AccessLevelAdminService(AcsDbContext db, WinPakClient winPak, AccessLevelSyncService sync, AuditService audit)
{
    public Task<IReadOnlyList<WinPakTimeZone>> GetTimeZonesAsync(CancellationToken ct = default)
        => winPak.GetTimeZonesAsync(ct);

    public async Task<AccessLevel> CreateAsync(string? userName, string name, string? description,
        IReadOnlyList<AccessLevelReaderAssignment> readers, CancellationToken ct = default)
    {
        name = Require(name);
        if (await db.AccessLevels.AnyAsync(a => a.IsActive && a.Name == name, ct))
            throw new InvalidOperationException($"Přístupová úroveň „{name}“ už existuje.");

        var definition = await BuildDefinitionAsync(name, description, readers, ct);
        await winPak.UpsertAccessLevelAsync(null, definition, ct);

        // WIN-PAK nevrací id nové úrovně — zrcadlo se obnoví ze seznamu a úroveň se najde podle názvu.
        await sync.SyncAsync(userName, ct: ct);
        var level = await db.AccessLevels.Include(a => a.Entries).FirstOrDefaultAsync(a => a.IsActive && a.Name == name, ct)
                    ?? throw new InvalidOperationException(
                        $"WIN-PAK zápis přijal, ale úroveň „{name}“ pak v seznamu není. Zkontrolujte ji ve WIN-PAKu.");

        await StoreDefinitionAsync(level, definition, ct);
        await MapSingleReaderAsync(level, ct);
        await audit.LogAsync(userName, "access-level-created", "AccessLevel", level.Id.ToString(),
            $"{name}: {definition.ReaderIds.Count} čteček", ct);
        return level;
    }

    public async Task UpdateAsync(string? userName, int id, string name, string? description,
        IReadOnlyList<AccessLevelReaderAssignment> readers, CancellationToken ct = default)
    {
        name = Require(name);
        var level = await db.AccessLevels.Include(a => a.Entries).FirstAsync(a => a.Id == id, ct);
        var definition = await BuildDefinitionAsync(name, description, readers, ct);

        await winPak.UpsertAccessLevelAsync(level.ExternalId, definition, ct);

        level.Name = name;
        level.Description = description;
        await StoreDefinitionAsync(level, definition, ct);
        try
        {
            await sync.RefreshAsync(level, ct);
        }
        catch (Exception)
        {
            // Strom je jen potvrzení; definice, kterou jsme zapsali, už v zrcadle je.
        }

        await MapSingleReaderAsync(level, ct);
        await audit.LogAsync(userName, "access-level-updated", "AccessLevel", level.Id.ToString(),
            $"{name}: {definition.ReaderIds.Count} čteček", ct);
    }

    public async Task DeleteAsync(string? userName, int id, CancellationToken ct = default)
    {
        var level = await db.AccessLevels.FirstAsync(a => a.Id == id, ct);
        await winPak.DeleteAccessLevelAsync(level.Name, ct);

        level.IsActive = false;
        var mapped = await db.Readers.Where(r => r.AccessLevelExternalId == level.ExternalId).ToListAsync(ct);
        foreach (var reader in mapped)
            reader.AccessLevelExternalId = null;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(userName, "access-level-deleted", "AccessLevel", level.Id.ToString(),
            mapped.Count == 0 ? level.Name : $"{level.Name}; odmapováno čteček: {mapped.Count}", ct);
    }

    private static string Require(string? name)
        => string.IsNullOrWhiteSpace(name) ? throw new InvalidOperationException("Název úrovně je povinný.") : name.Trim();

    private async Task<WinPakAccessLevelDefinition> BuildDefinitionAsync(string name, string? description,
        IReadOnlyList<AccessLevelReaderAssignment> readers, CancellationToken ct)
    {
        var ids = readers.Select(r => r.ReaderId).Distinct().ToList();
        var known = await db.Readers.Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);

        var readerIds = new List<string>();
        var timeZoneIds = new List<string>();
        foreach (var assignment in readers.DistinctBy(r => (r.ReaderId, r.TimeZoneExternalId)))
        {
            if (!known.TryGetValue(assignment.ReaderId, out var reader))
                throw new InvalidOperationException($"Čtečka {assignment.ReaderId} neexistuje.");
            if (string.IsNullOrWhiteSpace(reader.ExternalId))
                throw new InvalidOperationException($"Čtečka „{reader.Name}“ nemá id ve WIN-PAKu — do úrovně ji nelze zařadit.");
            if (string.IsNullOrWhiteSpace(assignment.TimeZoneExternalId))
                throw new InvalidOperationException($"U čtečky „{reader.Name}“ chybí časová zóna.");

            readerIds.Add(reader.ExternalId);
            timeZoneIds.Add(assignment.TimeZoneExternalId);
        }

        return new WinPakAccessLevelDefinition(name, description, readerIds, timeZoneIds);
    }

    /// <summary>Zapsanou definici uloží do zrcadla hned — strom z WIN-PAKu ji jen potvrdí.</summary>
    private async Task StoreDefinitionAsync(AccessLevel level, WinPakAccessLevelDefinition definition, CancellationToken ct)
    {
        var readers = await db.Readers.Where(r => r.ExternalId != null && definition.ReaderIds.Contains(r.ExternalId))
            .ToDictionaryAsync(r => r.ExternalId!, ct);
        var timeZones = (await SafeTimeZonesAsync(ct)).ToDictionary(t => t.Id);

        level.Entries.Clear();
        for (var i = 0; i < definition.ReaderIds.Count; i++)
        {
            level.Entries.Add(new AccessLevelEntry
            {
                ReaderExternalId = definition.ReaderIds[i],
                ReaderName = readers.GetValueOrDefault(definition.ReaderIds[i])?.Name,
                TimeZoneExternalId = definition.ReaderTimeZoneIds[i],
                TimeZoneName = timeZones.GetValueOrDefault(definition.ReaderTimeZoneIds[i])?.Name,
            });
        }

        level.LastSyncedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task<IReadOnlyList<WinPakTimeZone>> SafeTimeZonesAsync(CancellationToken ct)
    {
        try
        {
            return await winPak.GetTimeZonesAsync(ct);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Úroveň s jedinou čtečkou je úroveň té čtečky — doplní se jí mapování, pokud žádné nemá.</summary>
    private async Task MapSingleReaderAsync(AccessLevel level, CancellationToken ct)
    {
        if (level.Entries.Select(e => e.ReaderExternalId).Distinct().Count() != 1)
            return;

        var readerExternalId = level.Entries[0].ReaderExternalId;
        var reader = await db.Readers.FirstOrDefaultAsync(r => r.ExternalId == readerExternalId && r.AccessLevelExternalId == null, ct);
        if (reader is null)
            return;

        reader.AccessLevelExternalId = level.ExternalId;
        await db.SaveChangesAsync(ct);
    }
}
