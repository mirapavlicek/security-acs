using System.Diagnostics;
using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.WinPak;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acs.Infrastructure.Sync;

public record AccessLevelSyncResult(
    int Added, int Updated, int Deactivated, int ReadersMapped, int TreesFailed,
    int TreesLoaded = 0, int TreesSkipped = 0, string? LastTreeError = null,
    int EntriesPaired = 0, int EntriesUnknown = 0, int TreesReparsed = 0)
{
    public override string ToString()
    {
        var text = $"přidáno {Added}, aktualizováno {Updated}, deaktivováno {Deactivated}";
        if (TreesLoaded > 0)
            text += $", složení načteno u {TreesLoaded}";
        if (TreesReparsed > 0)
            text += $", složení přepočteno z uloženého stromu u {TreesReparsed}";
        if (EntriesPaired > 0)
            text += $", čteček ve stromech spárováno s ACS: {EntriesPaired}";
        if (EntriesUnknown > 0)
            text += $", čteček ve stromech ACS nezná: {EntriesUnknown}";
        if (ReadersMapped > 0)
            text += $", čtečkám doplněno mapování: {ReadersMapped}";
        if (TreesFailed > 0)
            text += $", strom přístupů se nepodařilo načíst u {TreesFailed}";
        if (TreesSkipped > 0)
            text += $" — čtení složení zastaveno, {TreesSkipped} úrovní se nečetlo";
        if (LastTreeError is not null)
            text += $" (poslední chyba: {LastTreeError})";
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
public class AccessLevelSyncService(AcsDbContext db, WinPakClient winPak, AuditService audit,
    ILogger<AccessLevelSyncService>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<AccessLevelSyncService>.Instance;

    /// <summary>
    /// Po kolika neúspěšných stromech v řadě (bez jediného úspěchu) se čtení složení vzdá.
    /// Když WIN-PAK neodpovídá, každý pokus trvá až do limitu konektoru (90 s) — u 55 úrovní
    /// by se čekalo přes hodinu na výsledek, který je jasný po třech pokusech.
    /// </summary>
    public const int GiveUpAfterConsecutiveFailures = 3;

    /// <summary>Stop i uprostřed úspěšného běhu, když WIN-PAK přestane odpovídat.</summary>
    public const int GiveUpAfterConsecutiveFailuresLater = 6;

    public async Task<AccessLevelSyncResult> SyncAsync(string? userName, bool refreshTrees = false,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Invoke("načítám seznam úrovní z WIN-PAKu…");
        var remote = await winPak.GetAccessLevelsAsync(ct);
        // Bez Include(Entries): 217 úrovní × 785 položek je 170 tisíc řádků — položky se
        // dotahují až u úrovně, jejíž složení se skutečně obnovuje (RefreshTreeAsync).
        var existing = await db.AccessLevels.Include(a => a.Tree).ToDictionaryAsync(a => a.ExternalId, ct);

        int added = 0, updated = 0, deactivated = 0;
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
                if (changed || refreshTrees || local.Tree?.AccessTree is null)
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
        _logger.LogInformation("Přístupové úrovně: seznam zrcadlen ({Count} úrovní), složení se načte u {ToRefresh}.",
            remote.Count, toRefresh.Count);

        int treesLoaded = 0, treesFailed = 0, treesSkipped = 0, consecutiveFailures = 0;
        string? lastError = null;
        var refreshed = new HashSet<int>();
        for (var i = 0; i < toRefresh.Count; i++)
        {
            var level = toRefresh[i];
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(Describe(i, toRefresh.Count, level.Name, treesLoaded, treesFailed, lastError));

            var watch = Stopwatch.StartNew();
            try
            {
                await RefreshTreeAsync(level, ct);
                await db.SaveChangesAsync(ct);
                refreshed.Add(level.Id);
                treesLoaded++;
                consecutiveFailures = 0;
                _logger.LogDebug("Strom úrovně „{Level}“ načten za {Elapsed:0.0} s ({Entries} položek).",
                    level.Name, watch.Elapsed.TotalSeconds, level.Entries.Count);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Strom je doplněk; úroveň sama je v zrcadle i bez něj.
                treesFailed++;
                consecutiveFailures++;
                lastError = ex.Message;
                _logger.LogWarning(ex, "Strom úrovně „{Level}“ se nepodařilo načíst (po {Elapsed:0.0} s).",
                    level.Name, watch.Elapsed.TotalSeconds);

                var giveUp = treesLoaded == 0
                    ? consecutiveFailures >= GiveUpAfterConsecutiveFailures
                    : consecutiveFailures >= GiveUpAfterConsecutiveFailuresLater;
                if (giveUp && i + 1 < toRefresh.Count)
                {
                    treesSkipped = toRefresh.Count - i - 1;
                    _logger.LogError("Čtení složení úrovní zastaveno po {Failures} neúspěších v řadě; {Skipped} úrovní se nečetlo. Poslední chyba: {Error}",
                        consecutiveFailures, treesSkipped, lastError);
                    break;
                }
            }
        }

        progress?.Invoke("přepočítávám složení z uložených stromů…");
        var reparsed = await ReparseStoredTreesAsync(
            existing.Values.Where(l => l.IsActive && !refreshed.Contains(l.Id)), ct);

        progress?.Invoke("páruji čtečky a doplňuji mapování…");
        var (paired, unknown) = await PairEntriesAsync(ct);
        var mapped = await MapSingleReaderLevelsAsync(ct);

        var result = new AccessLevelSyncResult(added, updated, deactivated, mapped, treesFailed, treesLoaded, treesSkipped, lastError,
            paired, unknown, reparsed);
        await audit.LogAsync(userName, "access-levels-synced", "AccessLevel", null, result.ToString(), ct);
        return result;
    }

    private ReaderMatcher? _matcher;
    private KnownTimeZones? _zones;

    private async Task<ReaderMatcher> MatcherAsync(CancellationToken ct)
        => _matcher ??= new ReaderMatcher(await db.Readers.ToListAsync(ct));

    /// <summary>
    /// Číselník zón WIN-PAKu (jedno volání za běh) — podle něj parser pozná, které čtečky
    /// ve stromu mají skutečnou zónu, a tedy patří do úrovně. Bez něj se rozhoduje jen
    /// podle toho, zda u čtečky zóna vůbec je.
    /// </summary>
    private async Task<KnownTimeZones> ZonesAsync(CancellationToken ct)
    {
        if (_zones is not null)
            return _zones;
        try
        {
            _zones = new KnownTimeZones(await winPak.GetTimeZonesAsync(ct));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Časové zóny se z WIN-PAKu nenačetly; čtečky ve stromech se posoudí jen podle toho, zda zónu mají.");
            _zones = KnownTimeZones.Empty;
        }
        return _zones;
    }

    /// <summary>
    /// Spáruje položky všech aktivních úrovní s čtečkami ACS — i ty načtené dřív, protože
    /// čtečky mohly přibýt (import z EKV, synchronizace) až po stromu. Vrací počet
    /// spárovaných položek a počet čteček ze stromů, které ACS nezná.
    /// </summary>
    public async Task<(int Paired, int Unknown)> PairEntriesAsync(CancellationToken ct = default)
    {
        var matcher = await MatcherAsync(ct);
        var entries = await db.AccessLevelEntries.Where(e => e.AccessLevel!.IsActive).ToListAsync(ct);

        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paired = 0;
        foreach (var entry in entries)
        {
            if (Pair(entry, matcher))
                paired++;
            else if (entry.ReaderId is null)
                unknown.Add(entry.ReaderName ?? entry.ReaderExternalId ?? "?");
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);
        return (paired, unknown.Count);
    }

    /// <summary>Doplní položce čtečku ACS (a z ní id WIN-PAKu, když ho strom nenesl). True = položka je spárovaná.</summary>
    private static bool Pair(AccessLevelEntry entry, ReaderMatcher matcher)
    {
        var reader = matcher.Match(entry);
        if (reader is null)
            return entry.ReaderId is not null;

        entry.ReaderId = reader.Id;
        entry.Reader = reader;
        if (entry.ReaderExternalId is null && !string.IsNullOrWhiteSpace(reader.ExternalId))
            entry.ReaderExternalId = reader.ExternalId;
        return true;
    }

    private static string Describe(int index, int total, string current, int loaded, int failed, string? lastError)
    {
        var text = $"složení {index + 1}/{total}, aktuálně „{current}“";
        if (loaded > 0)
            text += $", načteno {loaded}";
        if (failed > 0)
            text += $", selhalo {failed}" + (lastError is null ? "" : $" (poslední chyba: {lastError})");
        return text;
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

        (level.Tree ??= new AccessLevelTree { AccessLevelId = level.Id }).AccessTree = tree;
        var parsed = AccessTreeParser.Parse(tree, await ZonesAsync(ct));
        if (parsed is null)
            return; // strom je, ale není to XML, kterému rozumíme — položky nechat, surový strom je uložený

        await LoadEntriesAsync(level, ct);
        await ReplaceEntriesAsync(level, parsed, ct);
    }

    /// <summary>
    /// Složení z už uložených stromů znovu podle aktuálních pravidel čtení — bez volání
    /// WIN-PAKu. Strom je celý strom oblastí účtu a to, které čtečky z něj do úrovně patří,
    /// rozhoduje parser; když se jeho pravidla zpřesní (nebo přibudou zóny v číselníku),
    /// zrcadlo se srovná při příští synchronizaci, ne až po ručním „včetně složení všech úrovní“.
    /// Vrací počet úrovní, u kterých se složení změnilo.
    /// </summary>
    private async Task<int> ReparseStoredTreesAsync(IEnumerable<AccessLevel> levels, CancellationToken ct)
    {
        var changed = 0;
        foreach (var level in levels)
        {
            ct.ThrowIfCancellationRequested();
            var parsed = AccessTreeParser.Parse(level.Tree?.AccessTree, await ZonesAsync(ct));
            if (parsed is null)
                continue;

            await LoadEntriesAsync(level, ct);
            if (await ReplaceEntriesAsync(level, parsed, ct))
            {
                await db.SaveChangesAsync(ct);
                changed++;
                _logger.LogInformation("Složení úrovně „{Level}“ přepočteno z uloženého stromu: {Entries} položek.",
                    level.Name, level.Entries.Count);
            }
        }

        return changed;
    }

    /// <summary>Stávající položky jen této úrovně — Clear() nad nenačtenou kolekcí by v DB nechal staré řádky.</summary>
    private async Task LoadEntriesAsync(AccessLevel level, CancellationToken ct)
    {
        var entries = db.Entry(level).Collection(l => l.Entries);
        if (!entries.IsLoaded && level.Id != 0)
            await entries.LoadAsync(ct);
    }

    /// <summary>Nahradí položky úrovně spárovanými položkami ze stromu; false = složení je stejné a nic se neměnilo.</summary>
    private async Task<bool> ReplaceEntriesAsync(AccessLevel level, IReadOnlyList<AccessLevelEntry> parsed, CancellationToken ct)
    {
        var matcher = await MatcherAsync(ct);
        // Párování před porovnáním — spárovaná položka dostane id WIN-PAKu z čtečky, uložené položky ho už mají.
        foreach (var entry in parsed)
            Pair(entry, matcher);

        if (level.Entries.Select(Key).ToHashSet().SetEquals(parsed.Select(Key)))
            return false;

        level.Entries.Clear();
        foreach (var entry in parsed)
            level.Entries.Add(entry);
        return true;
    }

    private static (string?, string?, int?, string?, string?) Key(AccessLevelEntry e)
        => (e.ReaderExternalId, e.ReaderName, e.ReaderId, e.TimeZoneExternalId, e.TimeZoneName);

    /// <summary>
    /// Čtečkám bez mapování doplní úroveň, která obsahuje právě tu jednu čtečku.
    /// Jen aktivní úrovně a jen jednoznačné případy — když má čtečka takových úrovní víc,
    /// nechá se rozhodnutí správci.
    /// </summary>
    private async Task<int> MapSingleReaderLevelsAsync(CancellationToken ct)
    {
        // Úroveň „jedné čtečky“ = všechny položky spárované s touž čtečkou ACS (třeba ve dvou zónách).
        var levels = await db.AccessLevels
            .Where(a => a.IsActive && a.Entries.Count > 0 && a.Entries.All(e => e.ReaderId != null))
            .Select(a => new { a.ExternalId, ReaderIds = a.Entries.Select(e => e.ReaderId!.Value).ToList() })
            .ToListAsync(ct);

        var byReader = levels.Select(l => (l.ExternalId, ReaderIds: l.ReaderIds.Distinct().ToList()))
            .Where(l => l.ReaderIds.Count == 1)
            .GroupBy(l => l.ReaderIds[0])
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().ExternalId);
        if (byReader.Count == 0)
            return 0;

        var readerIds = byReader.Keys.ToList();
        var readers = await db.Readers
            .Where(r => readerIds.Contains(r.Id) && r.AccessLevelExternalId == null)
            .ToListAsync(ct);

        foreach (var reader in readers)
            reader.AccessLevelExternalId = byReader[reader.Id];

        if (readers.Count > 0)
            await db.SaveChangesAsync(ct);
        return readers.Count;
    }
}
