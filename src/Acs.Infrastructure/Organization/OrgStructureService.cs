using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Organization;

/// <summary>Výsledek přepočtu úseků a kategorií zaměstnanců.</summary>
public record OrgStructureResult(
    int UnitsAssigned, int UnitsUnmatched, int RanksChanged,
    int Directors, int Executives, int Managers, int Staff,
    IReadOnlyList<string> UnmatchedDepartments)
{
    public override string ToString()
        => $"úsek přiřazen {UnitsAssigned} zaměstnancům, bez úseku {UnitsUnmatched}; "
           + $"kategorie: ředitel {Directors}, přímo pod ředitelem {Executives}, vedoucí {Managers}, řadový {Staff}"
           + (RanksChanged > 0 ? $" (změněno {RanksChanged})" : "");
}

/// <summary>
/// Odvození organizační struktury pro schvalovací matici:
/// <list type="bullet">
///   <item><b>úsek zaměstnance</b> z textového oddělení (HR/AD) podle mapování na úsecích
///     (<see cref="OrgUnit.DepartmentPatterns"/>) — přesná shoda má přednost před předponou,
///     delší vzor před kratším; ručně zadaný úsek (<see cref="Employee.OrgUnitManual"/>) se nepřepisuje,</item>
///   <item><b>kategorie zaměstnance</b> z hierarchie nadřízených: ředitel = jediný vrchol
///     hierarchie s podřízenými (jinak se musí označit ručně), přímo podřízený řediteli = přímý
///     podřízený ředitele, vedoucí = má podřízené nebo vede úsek, ostatní řadoví. Ručně zadaná
///     kategorie (<see cref="Employee.RankManual"/>) se nepřepisuje.</item>
/// </list>
/// Volá se po importu zaměstnanců a ručně tlačítkem v číselníku úseků.
/// </summary>
public class OrgStructureService(AcsDbContext db, AuditService? audit = null)
{
    public async Task<OrgStructureResult> RecalculateAsync(string? userName, CancellationToken ct = default)
    {
        var units = await db.OrgUnits.Where(u => u.IsActive).ToListAsync(ct);
        var employees = await db.Employees.Where(e => e.IsActive).ToListAsync(ct);

        var (assigned, unmatched, unmatchedDepartments) = AssignUnits(units, employees);
        var (changed, counts) = DeriveRanks(units, employees);

        await db.SaveChangesAsync(ct);

        var result = new OrgStructureResult(
            assigned, unmatched, changed,
            counts[EmployeeRank.Director], counts[EmployeeRank.Executive],
            counts[EmployeeRank.Manager], counts[EmployeeRank.Staff],
            unmatchedDepartments);
        if (audit is not null)
            await audit.LogAsync(userName, "org-structure-recalculated", "Employee", null, result.ToString(), ct);
        return result;
    }

    /// <summary>Zařazení zaměstnanců do úseků podle mapování oddělení. Vrací i seznam nespárovaných oddělení.</summary>
    public static (int Assigned, int Unmatched, IReadOnlyList<string> UnmatchedDepartments) AssignUnits(
        IReadOnlyList<OrgUnit> units, IReadOnlyList<Employee> employees)
    {
        var patterns = units
            .SelectMany(u => u.DepartmentPatternList().Select(p => (Unit: u, Pattern: p)))
            .ToList();

        var assigned = 0;
        var unmatched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var employee in employees)
        {
            if (employee.OrgUnitManual)
            {
                if (employee.OrgUnitId is not null)
                    assigned++;
                continue;
            }

            OrgUnit? best = null;
            var bestScore = 0;
            foreach (var (unit, pattern) in patterns)
            {
                var score = OrgUnit.MatchDepartment(pattern, employee.Department);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = unit;
                }
            }

            if (best is not null)
            {
                employee.OrgUnitId = best.Id;
                assigned++;
            }
            else
            {
                employee.OrgUnitId = null;
                if (!string.IsNullOrWhiteSpace(employee.Department))
                    unmatched.Add(employee.Department.Trim());
            }
        }

        return (assigned, employees.Count - assigned, unmatched.ToList());
    }

    /// <summary>Odvození kategorií z hierarchie; vrací počet změn a četnosti kategorií.</summary>
    public static (int Changed, Dictionary<EmployeeRank, int> Counts) DeriveRanks(
        IReadOnlyList<OrgUnit> units, IReadOnlyList<Employee> employees)
    {
        var byId = employees.ToDictionary(e => e.Id);
        var hasSubordinates = employees
            .Where(e => e.ManagerId is not null && byId.ContainsKey(e.ManagerId.Value))
            .Select(e => e.ManagerId!.Value)
            .ToHashSet();
        foreach (var unit in units)
        {
            if (unit.HeadEmployeeId is { } head)
                hasSubordinates.Add(head);
        }

        // Ředitel: ručně označení, jinak jediný vrchol hierarchie, který někoho řídí.
        var directors = employees.Where(e => e.RankManual && e.Rank == EmployeeRank.Director).Select(e => e.Id).ToHashSet();
        if (directors.Count == 0)
        {
            var roots = employees
                .Where(e => (e.ManagerId is null || !byId.ContainsKey(e.ManagerId.Value)) && hasSubordinates.Contains(e.Id))
                .ToList();
            if (roots.Count == 1)
                directors.Add(roots[0].Id);
        }

        var changed = 0;
        var counts = new Dictionary<EmployeeRank, int>
        {
            [EmployeeRank.Director] = 0, [EmployeeRank.Executive] = 0,
            [EmployeeRank.Manager] = 0, [EmployeeRank.Staff] = 0,
        };
        foreach (var employee in employees)
        {
            EmployeeRank rank;
            if (employee.RankManual && employee.Rank != EmployeeRank.Auto)
            {
                rank = employee.Rank;
            }
            else
            {
                if (directors.Contains(employee.Id))
                    rank = EmployeeRank.Director;
                else if (employee.ManagerId is { } managerId && directors.Contains(managerId))
                    rank = EmployeeRank.Executive;
                else if (hasSubordinates.Contains(employee.Id))
                    rank = EmployeeRank.Manager;
                else
                    rank = EmployeeRank.Staff;

                if (employee.Rank != rank)
                {
                    employee.Rank = rank;
                    changed++;
                }
            }

            counts[rank]++;
        }

        return (changed, counts);
    }

    /// <summary>Id úseku a všech jeho předků (včetně sebe) — pro test „stejná linie úseků“.</summary>
    public static HashSet<int> AncestorsAndSelf(int unitId, IReadOnlyDictionary<int, int?> parents)
    {
        var result = new HashSet<int>();
        int? current = unitId;
        while (current is { } id && result.Add(id))
            current = parents.TryGetValue(id, out var parent) ? parent : null;
        return result;
    }
}
