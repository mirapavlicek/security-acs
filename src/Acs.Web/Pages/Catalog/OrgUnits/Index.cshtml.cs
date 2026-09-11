using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Organization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog.OrgUnits;

/// <summary>Řádek stromu úseků pro zobrazení (odsazení podle hloubky).</summary>
public record OrgUnitRow(OrgUnit Unit, int Depth, int Employees, int Areas);

public class IndexModel(AcsDbContext db, AuditService audit, OrgStructureService structure) : PageModel
{
    public List<OrgUnitRow> Rows { get; private set; } = [];
    public List<OrgUnit> AllUnits { get; private set; } = [];

    /// <summary>Oddělení zaměstnanců (z HR/AD), která zatím žádný úsek nemapuje — s počty.</summary>
    public List<(string Department, int Count)> UnmatchedDepartments { get; private set; } = [];

    public int EmployeesActive { get; private set; }
    public int EmployeesWithUnit { get; private set; }
    public Dictionary<EmployeeRank, int> RankCounts { get; private set; } = new();

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        AllUnits = await db.OrgUnits.Include(u => u.HeadEmployee).OrderBy(u => u.SortOrder).ThenBy(u => u.Name).ToListAsync();

        var employeeCounts = await db.Employees.Where(e => e.IsActive && e.OrgUnitId != null)
            .GroupBy(e => e.OrgUnitId!.Value).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
        var areaCounts = new Dictionary<int, int>();
        foreach (var (id, count) in await CountAreasAsync())
            areaCounts[id] = count;

        Rows = [];
        void Walk(int? parentId, int depth)
        {
            foreach (var unit in AllUnits.Where(u => u.ParentId == parentId))
            {
                Rows.Add(new OrgUnitRow(unit, depth, employeeCounts.GetValueOrDefault(unit.Id), areaCounts.GetValueOrDefault(unit.Id)));
                if (depth < 20)
                    Walk(unit.Id, depth + 1);
            }
        }

        Walk(null, 0);
        // Úseky s neplatným rodičem (nemělo by nastat) — ať nezmizí.
        foreach (var orphan in AllUnits.Where(u => Rows.All(r => r.Unit.Id != u.Id)))
            Rows.Add(new OrgUnitRow(orphan, 0, employeeCounts.GetValueOrDefault(orphan.Id), areaCounts.GetValueOrDefault(orphan.Id)));

        EmployeesActive = await db.Employees.CountAsync(e => e.IsActive);
        EmployeesWithUnit = await db.Employees.CountAsync(e => e.IsActive && e.OrgUnitId != null);
        RankCounts = (await db.Employees.Where(e => e.IsActive)
                .GroupBy(e => e.Rank).Select(g => new { g.Key, Count = g.Count() }).ToListAsync())
            .ToDictionary(x => x.Key, x => x.Count);

        UnmatchedDepartments = (await db.Employees
                .Where(e => e.IsActive && e.OrgUnitId == null && e.Department != null && e.Department != "")
                .GroupBy(e => e.Department!)
                .Select(g => new { g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).ThenBy(x => x.Key)
                .Take(100)
                .ToListAsync())
            .Select(x => (x.Key, x.Count))
            .ToList();
    }

    private async Task<List<(int UnitId, int Count)>> CountAreasAsync()
    {
        var result = new List<(int, int)>();
        result.AddRange((await db.Readers.Where(r => r.OrgUnitId != null).GroupBy(r => r.OrgUnitId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToListAsync()).Select(x => (x.Key, x.Count)));
        result.AddRange((await db.Rooms.Where(r => r.OrgUnitId != null).GroupBy(r => r.OrgUnitId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToListAsync()).Select(x => (x.Key, x.Count)));
        result.AddRange((await db.Corridors.Where(r => r.OrgUnitId != null).GroupBy(r => r.OrgUnitId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToListAsync()).Select(x => (x.Key, x.Count)));
        result.AddRange((await db.Floors.Where(r => r.OrgUnitId != null).GroupBy(r => r.OrgUnitId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToListAsync()).Select(x => (x.Key, x.Count)));
        result.AddRange((await db.Buildings.Where(r => r.OrgUnitId != null).GroupBy(r => r.OrgUnitId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToListAsync()).Select(x => (x.Key, x.Count)));
        result.AddRange((await db.ReaderGroups.Where(r => r.OrgUnitId != null).GroupBy(r => r.OrgUnitId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToListAsync()).Select(x => (x.Key, x.Count)));
        return result.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).ToList();
    }

    public async Task<IActionResult> OnPostCreateAsync(string name, string? code, int? parentId)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorMessage = "Zadejte název úseku.";
            return RedirectToPage();
        }

        var maxOrder = await db.OrgUnits.Where(u => u.ParentId == parentId).MaxAsync(u => (int?)u.SortOrder) ?? 0;
        var unit = new OrgUnit
        {
            Name = name.Trim(),
            Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim(),
            ParentId = parentId,
            SortOrder = maxOrder + 1,
        };
        db.OrgUnits.Add(unit);
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "orgunit-created", "OrgUnit", unit.Id.ToString(), unit.Name);
        return RedirectToPage("Edit", new { id = unit.Id });
    }

    /// <summary>Přepočet úseků (z oddělení) a kategorií (z hierarchie) — jinak běží po importu zaměstnanců.</summary>
    public async Task<IActionResult> OnPostRecalculateAsync()
    {
        var result = await structure.RecalculateAsync(User.Identity?.Name);
        Message = $"Přepočítáno: {result}.";
        return RedirectToPage();
    }
}
