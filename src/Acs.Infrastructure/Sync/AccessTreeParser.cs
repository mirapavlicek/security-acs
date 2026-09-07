using System.Xml.Linq;
using Acs.Domain.Entities;

namespace Acs.Infrastructure.Sync;

/// <summary>
/// Čtení stromu přístupů, jak ho vrací WIN-PAK (<c>GetAccessTreeByName</c>). Příručka
/// jeho podobu nepopisuje; na dosud viděných instalacích je to XML s prvky pro čtečky
/// (název, id zařízení) a časové zóny. Parser proto hledá známé názvy atributů a
/// prvků bez ohledu na velikost písmen a strukturu — co nepozná, nechá být a surový
/// strom zůstává uložený u úrovně pro kontrolu očima.
/// </summary>
public static class AccessTreeParser
{
    private static readonly string[] ReaderIdKeys = ["hwdeviceid", "readerid", "deviceid", "entranceid", "hid"];
    private static readonly string[] ReaderNameKeys = ["readername", "devicename", "entrancename", "reader", "device", "entrance"];
    private static readonly string[] TimeZoneIdKeys = ["timezoneid", "tzid", "timezone_id"];
    private static readonly string[] TimeZoneNameKeys = ["timezonename", "tzname", "timezone", "tz"];

    /// <summary>Položky stromu; prázdný seznam = úroveň bez čteček, null = strom není čitelné XML.</summary>
    public static IReadOnlyList<AccessLevelEntry>? Parse(string? tree)
    {
        if (string.IsNullOrWhiteSpace(tree) || !tree.TrimStart().StartsWith('<'))
            return null;

        XDocument document;
        try
        {
            document = XDocument.Parse(tree);
        }
        catch (Exception)
        {
            return null;
        }

        var entries = new List<AccessLevelEntry>();
        foreach (var element in document.Descendants())
        {
            var values = Values(element);
            var readerId = First(values, ReaderIdKeys);
            var readerName = ReaderName(element, values);
            if (readerId is null && readerName is null)
                continue;

            // Časová zóna bývá u čtečky, nebo jako nadřazený/vnořený prvek.
            var context = Values(element, includeAncestors: true, includeDescendants: true);
            entries.Add(new AccessLevelEntry
            {
                ReaderExternalId = readerId,
                ReaderName = readerName,
                TimeZoneExternalId = First(context, TimeZoneIdKeys),
                TimeZoneName = First(context, TimeZoneNameKeys),
            });
        }

        // Stejná čtečka může být ve stromu víckrát (jednou za každou zónu) — to je legitimní; duplicitní řádky ne.
        return entries
            .GroupBy(e => (e.ReaderExternalId, e.ReaderName, e.TimeZoneExternalId, e.TimeZoneName))
            .Select(g => g.First())
            .ToList();
    }

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
