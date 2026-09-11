using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog.OrgUnits;

public class EditModel(AcsDbContext db, AuditService audit) : PageModel
{
    public OrgUnit Unit { get; private set; } = null!;
    public List<OrgUnit> ParentCandidates { get; private set; } = [];
    public List<Employee> Employees { get; private set; } = [];
    public List<Employee> Members { get; private set; } = [];
    public int MembersTotal { get; private set; }
    public List<OrgUnit> Children { get; private set; } = [];

    /// <summary>Prostory přiřazené přímo k úseku (pro orientaci, co úsek „vlastní“).</summary>
    public List<string> Areas { get; private set; } = [];

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var unit = await db.OrgUnits.Include(u => u.Parent).Include(u => u.HeadEmployee)
            .FirstOrDefaultAsync(u => u.Id == id);
        if (unit is null)
            return NotFound();

        Unit = unit;
        var all = await db.OrgUnits.OrderBy(u => u.Name).ToListAsync();
        var descendants = Descendants(id, all);
        ParentCandidates = all.Where(u => u.Id != id && !descendants.Contains(u.Id)).ToList();
        Children = all.Where(u => u.ParentId == id).ToList();
        Employees = await db.Employees.Where(e => e.IsActive)
            .OrderBy(e => e.LastName).ThenBy(e => e.FirstName).ToListAsync();
        MembersTotal = await db.Employees.CountAsync(e => e.OrgUnitId == id && e.IsActive);
        Members = await db.Employees.Where(e => e.OrgUnitId == id && e.IsActive)
            .OrderBy(e => e.LastName).ThenBy(e => e.FirstName).Take(50).ToListAsync();

        Areas = [];
        Areas.AddRange(await db.Buildings.Where(b => b.OrgUnitId == id).Select(b => "budova " + b.Name).ToListAsync());
        Areas.AddRange(await db.Floors.Where(f => f.OrgUnitId == id).Select(f => "patro " + f.Building!.Name + " / " + f.Name).ToListAsync());
        Areas.AddRange(await db.Corridors.Where(c => c.OrgUnitId == id).Select(c => "chodba " + c.Name).ToListAsync());
        Areas.AddRange(await db.Rooms.Where(r => r.OrgUnitId == id).Select(r => "místnost " + r.Name).Take(100).ToListAsync());
        Areas.AddRange(await db.Readers.Where(r => r.OrgUnitId == id).Select(r => "čtečka " + r.Name).Take(100).ToListAsync());
        Areas.AddRange(await db.ReaderGroups.Where(g => g.OrgUnitId == id).Select(g => "skupina " + g.Name).ToListAsync());
        return Page();
    }

    private static HashSet<int> Descendants(int id, List<OrgUnit> all)
    {
        var result = new HashSet<int>();
        var stack = new Stack<int>([id]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var child in all.Where(u => u.ParentId == current))
            {
                if (result.Add(child.Id))
                    stack.Push(child.Id);
            }
        }

        return result;
    }

    public async Task<IActionResult> OnPostAsync(int id, string name, string? code, string? description,
        int? parentId, int? headEmployeeId, string? departmentPatterns, string? isActive, int sortOrder)
    {
        var unit = await db.OrgUnits.FindAsync(id);
        if (unit is null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorMessage = "Název je povinný.";
            return RedirectToPage(new { id });
        }

        if (parentId is not null)
        {
            var all = await db.OrgUnits.ToListAsync();
            if (parentId == id || Descendants(id, all).Contains(parentId.Value))
            {
                ErrorMessage = "Nadřazený úsek nemůže být úsek sám ani jeho podřízený úsek — vznikl by cyklus.";
                return RedirectToPage(new { id });
            }
        }

        if (headEmployeeId is not null && !await db.Employees.AnyAsync(e => e.Id == headEmployeeId))
        {
            ErrorMessage = "Vybraný vedoucí neexistuje.";
            return RedirectToPage(new { id });
        }

        unit.Name = name.Trim();
        unit.Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        unit.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        unit.ParentId = parentId;
        unit.HeadEmployeeId = headEmployeeId;
        unit.DepartmentPatterns = string.IsNullOrWhiteSpace(departmentPatterns) ? null : departmentPatterns.Trim();
        unit.IsActive = isActive == "true";
        unit.SortOrder = sortOrder;
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "orgunit-updated", "OrgUnit", id.ToString(), unit.Name);
        Message = "Úsek uložen. Zařazení zaměstnanců podle mapování se projeví po přepočtu (Úseky → Přepočítat) nebo po importu.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var unit = await db.OrgUnits.FindAsync(id);
        if (unit is null)
            return NotFound();

        if (await db.OrgUnits.AnyAsync(u => u.ParentId == id))
        {
            ErrorMessage = "Úsek má podřízené úseky — nejdřív je přesuňte nebo smažte.";
            return RedirectToPage(new { id });
        }

        db.OrgUnits.Remove(unit);   // vazby zaměstnanců a prostorů se vynulují (SetNull)
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "orgunit-deleted", "OrgUnit", id.ToString(), unit.Name);
        Message = $"Úsek {unit.Name} smazán.";
        return RedirectToPage("Index");
    }
}
