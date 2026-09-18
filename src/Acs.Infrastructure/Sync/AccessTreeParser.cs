using System.Xml.Linq;
using Acs.Domain.Entities;
using Acs.Infrastructure.WinPak;

namespace Acs.Infrastructure.Sync;

/// <summary>
/// Časové zóny, které WIN-PAK skutečně má (<c>GET /api/v1/time-zones</c>). Podle nich parser
/// pozná, zda čtečka ve stromu přístup má: strom je celý strom přístupových oblastí účtu
/// a čtečka bez skutečné zóny je v něm jen jako položka nabídky, ne jako součást úrovně.
/// Bez seznamu (WIN-PAK ho nevrátil) se rozhoduje jen podle toho, zda zóna vůbec je.
/// </summary>
public sealed class KnownTimeZones
{
    public static readonly KnownTimeZones Empty = new([]);

    private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    public KnownTimeZones(IEnumerable<WinPakTimeZone> zones)
    {
        foreach (var zone in zones)
        {
            if (!string.IsNullOrWhiteSpace(zone.Id))
                _ids.Add(zone.Id.Trim());
            if (!string.IsNullOrWhiteSpace(zone.Name))
                _names.Add(zone.Name.Trim());
        }
    }

    public bool IsEmpty => _ids.Count == 0 && _names.Count == 0;
    public bool ContainsId(string id) => _ids.Contains(id.Trim());
    public bool ContainsName(string name) => _names.Contains(name.Trim());
}

/// <summary>
/// Čtení stromu přístupů, jak ho vrací WIN-PAK (<c>GetAccessTreeByName</c>). Příručka
/// jeho podobu nepopisuje; na dosud viděných instalacích je to XML s prvky pro čtečky
/// (název, id zařízení) a časové zóny. Parser proto hledá známé názvy atributů a
/// prvků bez ohledu na velikost písmen a strukturu — co nepozná, nechá být a surový
/// strom zůstává uložený u úrovně pro kontrolu očima.
///
/// Strom není seznam čteček úrovně, ale <b>celý strom přístupových oblastí</b> účtu
/// (tak úroveň vypadá i v okně WIN-PAKu: všechny čtečky a u každé buď časová zóna,
/// nebo nic). Do úrovně patří jen čtečky, u kterých strom skutečnou zónu nese —
/// ostatní by z každé úrovně udělaly „všechny čtečky v domě“.
/// </summary>
public static class AccessTreeParser
{
    private static readonly string[] ReaderIdKeys = ["hwdeviceid", "readerid", "deviceid", "entranceid", "hid"];
    private static readonly string[] ReaderNameKeys = ["readername", "devicename", "entrancename", "reader", "device", "entrance"];
    private static readonly string[] TimeZoneIdKeys = ["timezoneid", "tzid", "timezone_id"];
    private static readonly string[] TimeZoneNameKeys = ["timezonename", "tzname", "timezone", "tz"];

    /// <summary>
    /// Zástupné texty místo zóny u čtečky, na kterou úroveň přístup nedává (po odstranění
    /// ozdob typu <c>** None **</c>). „Never“ je sice zóna, ale čtečka s ní nikdy neotevře —
    /// pro ACS je to totéž jako bez přístupu. Skutečné zóny („Always“, „Pracovní doba“) sem nepatří.
    /// </summary>
    private static readonly HashSet<string> NoAccessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "no access", "noaccess", "not assigned", "unassigned", "not set", "null", "n/a", "-", "--", "never",
        "žádná", "žádný", "žádné", "bez přístupu", "nepřiřazeno", "nepřiřazena", "nepřiděleno", "nikdy",
    };

    /// <summary>Čtečka ve stromu tak, jak ji WIN-PAK zapsal, včetně toho, zda na ni úroveň dává přístup.</summary>
    internal sealed record TreeReader(string? Id, string? Name, string? TimeZoneId, string? TimeZoneName, bool HasAccess);

    /// <summary>
    /// Položky úrovně — jen čtečky, na které úroveň dává přístup. Prázdný seznam = úroveň bez
    /// čteček, null = strom není čitelné XML. <paramref name="zones"/> zpřesní rozhodování
    /// (zóna, kterou WIN-PAK nemá, není zóna).
    /// </summary>
    public static IReadOnlyList<AccessLevelEntry>? Parse(string? tree, KnownTimeZones? zones = null)
    {
        var document = Load(tree);
        if (document is null)
            return null;

        // Stejná čtečka může být ve stromu víckrát (jednou za každou zónu) — to je legitimní; duplicitní řádky ne.
        return Readers(document, zones)
            .Where(r => r.HasAccess)
            .Select(r => new AccessLevelEntry
            {
                ReaderExternalId = r.Id,
                ReaderName = r.Name,
                TimeZoneExternalId = r.TimeZoneId,
                TimeZoneName = r.TimeZoneName,
            })
            .GroupBy(e => (e.ReaderExternalId, e.ReaderName, e.TimeZoneExternalId, e.TimeZoneName))
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>Počet různých čteček ve stromu bez ohledu na přístup (kolik čteček WIN-PAK v účtu nabízí); null = strom nečitelný.</summary>
    public static int? CountReaders(string? tree)
    {
        var document = Load(tree);
        return document is null ? null : Readers(document, null).Select(r => r.Name ?? r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    internal static XDocument? Load(string? tree)
    {
        if (string.IsNullOrWhiteSpace(tree) || !tree.TrimStart().StartsWith('<'))
            return null;

        try
        {
            return XDocument.Parse(tree);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static IEnumerable<TreeReader> Readers(XDocument document, KnownTimeZones? zones)
    {
        foreach (var element in document.Descendants())
        {
            if (TryReadReader(element, Values(element), zones, out var reader))
                yield return reader;
        }
    }

    /// <summary>Prvek stromu jako čtečka; false = prvek čtečku nepopisuje (větev, obal…).</summary>
    internal static bool TryReadReader(XElement element, Dictionary<string, string> values, KnownTimeZones? zones, out TreeReader reader)
    {
        var readerId = First(values, ReaderIdKeys);
        var readerName = ReaderName(element, values);
        if (readerId is null && readerName is null)
        {
            reader = null!;
            return false;
        }

        // Časová zóna bývá u čtečky, nebo jako nadřazený/vnořený prvek.
        var context = Values(element, includeAncestors: true, includeDescendants: true);
        var timeZoneId = First(context, TimeZoneIdKeys);
        var timeZoneName = First(context, TimeZoneNameKeys);
        reader = new TreeReader(readerId, readerName, timeZoneId, timeZoneName, GrantsAccess(timeZoneId, timeZoneName, zones));
        return true;
    }

    /// <summary>
    /// Čtečka je součástí úrovně, jen když má skutečnou časovou zónu: ne prázdnou, ne zástupný
    /// text („None“), ne id 0, a když je k dispozici číselník zón WIN-PAKu, tak zónu z něj.
    /// </summary>
    internal static bool GrantsAccess(string? timeZoneId, string? timeZoneName, KnownTimeZones? zones)
    {
        zones ??= KnownTimeZones.Empty;
        var nameIsReal = timeZoneName is not null && !NoAccessNames.Contains(Strip(timeZoneName));

        if (timeZoneId is not null)
        {
            if (IsNoAccessId(timeZoneId))
                return false;
            if (zones.IsEmpty || zones.ContainsId(timeZoneId))
                return true;
            return nameIsReal && zones.ContainsName(timeZoneName!);
        }

        if (!nameIsReal)
            return false;
        return zones.IsEmpty || zones.ContainsName(timeZoneName!);
    }

    private static bool IsNoAccessId(string timeZoneId)
        => long.TryParse(timeZoneId.Trim(), out var id) && id <= 0;

    /// <summary>„** None **“, „<none>“, „[None]“ → „none“.</summary>
    private static string Strip(string value)
        => string.Join(' ', value.Trim('*', '<', '>', '[', ']', '(', ')', ' ', '\t')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Název čtečky: pojmenovaný atribut/prvek (<c>ReaderName</c>, <c>DeviceName</c>…), nebo — u prvku,
    /// který je sám čtečkou (<c>&lt;Reader&gt;</c>) — obecné <c>Name</c> jako atribut i podřízený prvek.
    /// Tak vypadá strom z WIN-PAKu 4.9: <c>&lt;Reader&gt;&lt;Name&gt;334001&lt;/Name&gt;&lt;Parent&gt;23 MOC&lt;/Parent&gt;&lt;Timezone&gt;Always On&lt;/Timezone&gt;&lt;/Reader&gt;</c>.
    /// </summary>
    internal static string? ReaderName(XElement element, Dictionary<string, string> values)
    {
        var named = First(values, ReaderNameKeys);
        if (named is not null)
            return named;
        if (!IsReaderElement(element))
            return null;
        var generic = First(values, ["name"]);
        return string.IsNullOrWhiteSpace(generic) ? null : generic;
    }

    internal static bool IsReaderElement(XElement element)
    {
        var name = element.Name.LocalName.ToLowerInvariant();
        return name.Contains("reader") || name.Contains("entrance") || name.Contains("device");
    }

    internal static string? First(Dictionary<string, string> values, string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    /// <summary>Atributy a jednoduché podřízené prvky jako slovník s klíči malými písmeny.</summary>
    internal static Dictionary<string, string> Values(XElement element, bool includeAncestors = false, bool includeDescendants = false)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(XElement source)
        {
            foreach (var attribute in source.Attributes())
                values.TryAdd(attribute.Name.LocalName.ToLowerInvariant(), attribute.Value);
            foreach (var child in source.Elements().Where(c => !c.HasElements))
                values.TryAdd(child.Name.LocalName.ToLowerInvariant(), child.Value);
        }

        Add(element);
        if (includeDescendants)
        {
            foreach (var descendant in element.Descendants())
                Add(descendant);
        }

        if (includeAncestors)
        {
            foreach (var ancestor in element.Ancestors())
                Add(ancestor);
        }

        return values;
    }
}
