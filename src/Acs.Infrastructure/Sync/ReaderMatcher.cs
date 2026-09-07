using Acs.Domain.Entities;

namespace Acs.Infrastructure.Sync;

/// <summary>
/// Páruje čtečky ze stromu přístupů WIN-PAKu s čtečkami ACS. Strom nese id zařízení
/// jen na některých instalacích; ve WIN-PAKu 4.9 (Motol) je v něm jen <c>Name</c> —
/// a to je číslo čtečky, které má ACS z dokumentace EKV jako <see cref="Reader.DeviceNumber"/>
/// (nebo jako název čtečky založené ze synchronizace). Páruje se tedy podle id, a když
/// id není nebo nesedí, podle čísla; nejednoznačná shoda (dvě čtečky se stejným číslem)
/// se nepáruje — rozhodne správce.
/// </summary>
public sealed class ReaderMatcher
{
    private readonly Dictionary<string, Reader?> _byExternalId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Reader?> _byNumber = new(StringComparer.OrdinalIgnoreCase);

    public ReaderMatcher(IEnumerable<Reader> readers)
    {
        foreach (var reader in readers)
        {
            if (!string.IsNullOrWhiteSpace(reader.ExternalId))
                Register(_byExternalId, reader.ExternalId, reader);

            if (!string.IsNullOrWhiteSpace(reader.DeviceNumber))
                Register(_byNumber, ReaderSyncService.Normalize(reader.DeviceNumber), reader);

            // Čtečka založená ze synchronizace má název = název zařízení ve WIN-PAKu (zpravidla číslo);
            // čtečka z výkresu „341011 — DÍLNA“ má číslo v úvodu.
            if (!string.IsNullOrWhiteSpace(reader.Name))
                Register(_byNumber, ReaderSyncService.Normalize(reader.Name), reader);
            var leading = LeadingToken(reader.Name);
            if (leading is not null)
                Register(_byNumber, leading, reader);
        }
    }

    /// <summary>Čtečka ACS pro položku stromu, nebo null (neznámá, nebo nejednoznačná).</summary>
    public Reader? Match(string? readerExternalId, string? readerName)
    {
        if (!string.IsNullOrWhiteSpace(readerExternalId) && _byExternalId.TryGetValue(readerExternalId, out var byId) && byId is not null)
            return byId;

        if (string.IsNullOrWhiteSpace(readerName))
            return null;

        if (_byNumber.TryGetValue(ReaderSyncService.Normalize(readerName), out var byNumber))
            return byNumber;

        var leading = LeadingToken(readerName);
        return leading is not null && _byNumber.TryGetValue(leading, out var byLeading) ? byLeading : null;
    }

    public Reader? Match(AccessLevelEntry entry) => Match(entry.ReaderExternalId, entry.ReaderName);

    /// <summary>Druhý výskyt stejného klíče shodu ruší (null) — párovat se smí jen jednoznačně.</summary>
    private static void Register(Dictionary<string, Reader?> index, string key, Reader reader)
    {
        if (index.TryGetValue(key, out var existing))
        {
            if (existing is not null && !ReferenceEquals(existing, reader))
                index[key] = null;
            return;
        }

        index[key] = reader;
    }

    /// <summary>Číselný úvod názvu („341011 — DÍLNA“ → „341011“); null, když název číslem nezačíná.</summary>
    private static string? LeadingToken(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var token = name.TrimStart();
        var end = 0;
        while (end < token.Length && char.IsDigit(token[end]))
            end++;
        if (end < 3 || (end < token.Length && char.IsLetterOrDigit(token[end])))
            return null;

        return ReaderSyncService.Normalize(token[..end]);
    }
}
