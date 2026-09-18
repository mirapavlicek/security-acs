using System.Xml.Linq;

namespace Acs.Infrastructure.Sync;

/// <summary>
/// Čtečka ve stromu přístupů: název (u čteček z EKV jejich číslo), id, časová zóna a zda na ni
/// úroveň dává přístup (strom nese všechny čtečky účtu, ne jen ty v úrovni — viz <see cref="AccessTreeParser"/>).
/// </summary>
public sealed record AccessTreeReader(string Name, string? ExternalId, string? TimeZoneName, bool HasAccess = true);

/// <summary>Větev stromu (areál → budova → …) s podřízenými větvemi a čtečkami.</summary>
public sealed class AccessTreeBranch(string name)
{
    public string Name { get; } = name;
    public List<AccessTreeBranch> Children { get; } = [];
    public List<AccessTreeReader> Readers { get; } = [];

    /// <summary>Čteček s přístupem ve větvi včetně podřízených — do záhlaví větve.</summary>
    public int ReaderCount => Readers.Count(r => r.HasAccess) + Children.Sum(c => c.ReaderCount);

    /// <summary>Čteček ve větvi včetně podřízených, na které úroveň přístup nedává.</summary>
    public int NoAccessCount => Readers.Count(r => !r.HasAccess) + Children.Sum(c => c.NoAccessCount);
}

/// <summary>
/// Strom přístupů pro zobrazení: hierarchie větví (WIN-PAK: <c>&lt;Branch&gt;&lt;Name&gt;23 MOC&lt;/Name&gt;&lt;Parent&gt;FN Motol&lt;/Parent&gt;&lt;/Branch&gt;</c>)
/// a čtečky zařazené pod svou větev (<c>&lt;Reader&gt;…&lt;Parent&gt;23 MOC&lt;/Parent&gt;</c>).
/// Stromy bez větví dají jeden kořen se všemi čtečkami. Čtečky bez přístupu zůstávají
/// v osnově označené (<see cref="AccessTreeReader.HasAccess"/>), aby správce viděl, co
/// WIN-PAK poslal a proč to v úrovni není. Položky k zápisu do zrcadla dělá
/// <see cref="AccessTreeParser"/>; tady jde jen o to, jak strom ukázat správci.
/// </summary>
public static class AccessTreeOutline
{
    private static readonly string[] ParentKeys = ["parent", "parentname", "branch", "area", "accessarea"];

    /// <summary>Kořeny stromu; null = strom není čitelné XML.</summary>
    public static IReadOnlyList<AccessTreeBranch>? Parse(string? tree, KnownTimeZones? zones = null)
    {
        var document = AccessTreeParser.Load(tree);
        if (document is null)
            return null;

        var branches = new Dictionary<string, AccessTreeBranch>(StringComparer.OrdinalIgnoreCase);
        var parentOf = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var readers = new List<(AccessTreeReader Reader, string? Parent)>();

        foreach (var element in document.Descendants())
        {
            var values = AccessTreeParser.Values(element);
            if (AccessTreeParser.TryReadReader(element, values, zones, out var reader))
            {
                readers.Add((new AccessTreeReader(reader.Name ?? reader.Id!, reader.Id, reader.TimeZoneName, reader.HasAccess),
                    Parent(element, values)));
                continue;
            }

            if (!IsBranchElement(element))
                continue;
            var name = AccessTreeParser.First(values, ["name", "branchname"]);
            if (name is null)
                continue;
            branches.TryAdd(name, new AccessTreeBranch(name));
            parentOf.TryAdd(name, Parent(element, values));
        }

        var roots = new List<AccessTreeBranch>();
        foreach (var (name, branch) in branches)
        {
            var parent = parentOf.GetValueOrDefault(name);
            if (parent is not null && branches.TryGetValue(parent, out var parentBranch) && parentBranch != branch)
                parentBranch.Children.Add(branch);
            else
                roots.Add(branch);
        }

        AccessTreeBranch? orphans = null;
        foreach (var (reader, parent) in readers.DistinctBy(r => (r.Reader, r.Parent)))
        {
            if (parent is not null && branches.TryGetValue(parent, out var branch))
            {
                branch.Readers.Add(reader);
                continue;
            }

            // Bez větví (jiný formát stromu) je kořenem sama úroveň.
            orphans ??= roots.Count == 0 ? new AccessTreeBranch("") : new AccessTreeBranch("Bez zařazení");
            orphans.Readers.Add(reader);
        }

        if (orphans is not null)
            roots.Add(orphans);

        return roots;
    }

    private static bool IsBranchElement(XElement element)
    {
        var name = element.Name.LocalName.ToLowerInvariant();
        return name.Contains("branch") || name.Contains("area") || name.Contains("group") || name.Contains("folder");
    }

    private static string? Parent(XElement element, Dictionary<string, string> values)
    {
        var parent = AccessTreeParser.First(values, ParentKeys);
        if (parent is not null)
            return parent;

        // Větev jako nadřazený prvek (vnořený zápis) — její jméno je jméno větve.
        var ancestor = element.Ancestors().FirstOrDefault(IsBranchElement);
        return ancestor is null ? null : AccessTreeParser.First(AccessTreeParser.Values(ancestor), ["name", "branchname"]);
    }
}
