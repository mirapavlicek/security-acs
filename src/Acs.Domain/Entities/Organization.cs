namespace Acs.Domain.Entities;

/// <summary>
/// Úsek (organizační jednotka): úsek náměstka → odbor / klinika → oddělení… Strom přes
/// <see cref="ParentId"/>. Úsek má vedoucího (= odpovědnou osobu) a mapování na textová
/// oddělení zaměstnanců z HR/AD, podle kterého se zaměstnanci do úseku zařazují při importu.
/// Prostory (čtečky, místnosti, patra, budovy, skupiny) se k úseku přiřazují, aby šlo
/// rozlišit vstup „v rámci vlastního úseku“ a „mimo vlastní úsek“ a najít odpovědnou
/// osobu cílového úseku.
/// </summary>
public class OrgUnit
{
    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>Krátký kód (např. „LPP“, „CHIR“).</summary>
    public string? Code { get; set; }
    public string? Description { get; set; }

    public int? ParentId { get; set; }
    public OrgUnit? Parent { get; set; }
    public List<OrgUnit> Children { get; set; } = [];

    /// <summary>Vedoucí úseku — odpovědná osoba prostorů úseku, pokud u nich není uvedena jiná.</summary>
    public int? HeadEmployeeId { get; set; }
    public Employee? HeadEmployee { get; set; }

    /// <summary>
    /// Mapování textových oddělení zaměstnanců (<see cref="Employee.Department"/>) na tento úsek —
    /// jeden vzor na řádek: přesný název, nebo předpona zakončená „*“ (např. „Chirurgická klinika*“).
    /// </summary>
    public string? DepartmentPatterns { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    /// <summary>Vzory oddělení bez prázdných řádků.</summary>
    public IReadOnlyList<string> DepartmentPatternList()
        => string.IsNullOrWhiteSpace(DepartmentPatterns)
            ? []
            : DepartmentPatterns.Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Odpovídá oddělení vzoru? Přesná shoda bez rozlišení velikosti písmen, nebo předpona
    /// (vzor končí „*“). Vrací „sílu“ shody (délka vzoru; přesná shoda má přednost), 0 = neshoda.
    /// </summary>
    public static int MatchDepartment(string pattern, string? department)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(department))
            return 0;
        var p = pattern.Trim();
        var d = department.Trim();
        if (p.EndsWith('*'))
        {
            var prefix = p[..^1].TrimEnd();
            return prefix.Length > 0 && d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? prefix.Length : 0;
        }

        return string.Equals(p, d, StringComparison.OrdinalIgnoreCase) ? p.Length + 1000 : 0;
    }

    /// <summary>„Kód · Název“ pro zobrazení.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Code) ? Name : $"{Code} · {Name}";
}

/// <summary>
/// Kategorie zaměstnance pro schvalovací matici — podle ní se liší schvalovací kroky
/// (řadový zaměstnanec vs. vedoucí vs. náměstek vs. ředitel). Odvozuje se z hierarchie
/// nadřízených (<see cref="Employee.ManagerId"/>), nebo se zadá ručně.
/// </summary>
public enum EmployeeRank
{
    /// <summary>Zatím neodvozeno — chová se jako <see cref="Staff"/>.</summary>
    Auto = 0,
    /// <summary>Řadový zaměstnanec (nikoho neřídí).</summary>
    Staff = 1,
    /// <summary>Vedoucí zaměstnanec (primář, vrchní sestra, vedoucí oddělení / odboru).</summary>
    Manager = 2,
    /// <summary>Náměstek nebo vedoucí zaměstnanec přímo podřízený řediteli.</summary>
    Executive = 3,
    /// <summary>Ředitel (vrchol hierarchie).</summary>
    Director = 4,
}

/// <summary>Bitová maska kategorií pro podmínku úrovně matice (0 = platí pro všechny).</summary>
[Flags]
public enum RankFlags
{
    None = 0,
    Staff = 1,
    Manager = 2,
    Executive = 4,
    Director = 8,
    All = Staff | Manager | Executive | Director,
}

public static class EmployeeRankExtensions
{
    /// <summary>Kategorie → příznak masky (<see cref="EmployeeRank.Auto"/> se bere jako řadový).</summary>
    public static RankFlags ToFlag(this EmployeeRank rank) => rank switch
    {
        EmployeeRank.Manager => RankFlags.Manager,
        EmployeeRank.Executive => RankFlags.Executive,
        EmployeeRank.Director => RankFlags.Director,
        _ => RankFlags.Staff,
    };

    public static string Label(this EmployeeRank rank) => rank switch
    {
        EmployeeRank.Staff => "řadový zaměstnanec",
        EmployeeRank.Manager => "vedoucí zaměstnanec",
        EmployeeRank.Executive => "přímo podřízený řediteli",
        EmployeeRank.Director => "ředitel",
        _ => "neurčeno (řadový)",
    };

    public static string Label(this RankFlags flags)
    {
        if (flags == RankFlags.None || flags == RankFlags.All)
            return "všechny kategorie";
        var parts = new List<string>();
        if (flags.HasFlag(RankFlags.Staff)) parts.Add("řadový");
        if (flags.HasFlag(RankFlags.Manager)) parts.Add("vedoucí");
        if (flags.HasFlag(RankFlags.Executive)) parts.Add("přímo pod ředitelem");
        if (flags.HasFlag(RankFlags.Director)) parts.Add("ředitel");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Prostor, který může mít vlastníka: úsek a odpovědnou osobu. Když je obojí prázdné,
/// dědí se z nadřazeného prostoru (čtečka → místnost / chodba → patro → budova).
/// </summary>
public interface IOwnedArea
{
    int? OrgUnitId { get; set; }
    int? ResponsibleEmployeeId { get; set; }
}
