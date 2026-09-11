using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog.Employees;

public class EditModel(AcsDbContext db, AuditService audit) : PageModel
{
    [BindProperty]
    public Employee Employee { get; set; } = new() { FirstName = "", LastName = "" };

    public List<EmployeeIdentifier> Identifiers { get; private set; } = [];

    /// <summary>Aktivní zaměstnanci pro výběr nadřízeného (bez editovaného zaměstnance).</summary>
    public List<Employee> ManagerCandidates { get; private set; } = [];

    /// <summary>Podřízení (zaměstnanci, kteří mají tohoto zaměstnance jako nadřízeného).</summary>
    public List<Employee> Subordinates { get; private set; } = [];

    /// <summary>Nadřízený zvolený ve formuláři (prázdné = bez nadřízeného).</summary>
    [BindProperty] public int? ManagerId { get; set; }

    /// <summary>Zadáno ručně — import z AD nadřízeného nepřepíše.</summary>
    [BindProperty] public bool ManagerManual { get; set; }

    /// <summary>Úsek zvolený ve formuláři (prázdné = bez úseku).</summary>
    [BindProperty] public int? OrgUnitId { get; set; }
    [BindProperty] public bool OrgUnitManual { get; set; }

    /// <summary>Kategorie zvolená ve formuláři (<see cref="EmployeeRank.Auto"/> = odvodit z hierarchie).</summary>
    [BindProperty] public EmployeeRank Rank { get; set; }

    public List<OrgUnit> OrgUnits { get; private set; } = [];

    public static readonly EmployeeRank[] Ranks =
        [EmployeeRank.Staff, EmployeeRank.Manager, EmployeeRank.Executive, EmployeeRank.Director];

    public IdentifierType[] Types { get; } =
        [IdentifierType.Card, IdentifierType.LicensePlate, IdentifierType.Pin,
         IdentifierType.Tag, IdentifierType.Biometric, IdentifierType.Other];

    [TempData] public string? ErrorMessage { get; set; }

    public string TypeName(IdentifierType type) => type switch
    {
        IdentifierType.Card => "Karta",
        IdentifierType.LicensePlate => "SPZ",
        IdentifierType.Pin => "PIN",
        IdentifierType.Tag => "Čip",
        IdentifierType.Biometric => "Biometrie",
        _ => "Jiný",
    };

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        await LoadManagerCandidatesAsync(id);
        if (id is null)
            return Page();

        var employee = await db.Employees.Include(e => e.Manager).FirstOrDefaultAsync(e => e.Id == id.Value);
        if (employee is null)
            return NotFound();

        Employee = employee;
        ManagerId = employee.ManagerId;
        ManagerManual = employee.ManagerManual;
        OrgUnitId = employee.OrgUnitId;
        OrgUnitManual = employee.OrgUnitManual;
        Rank = employee.RankManual ? employee.Rank : EmployeeRank.Auto;
        Subordinates = await db.Employees
            .Where(e => e.ManagerId == id.Value)
            .OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
            .ToListAsync();
        await LoadIdentifiersAsync(id.Value);
        return Page();
    }

    private async Task LoadManagerCandidatesAsync(int? exceptId)
    {
        ManagerCandidates = await db.Employees
            .Where(e => e.IsActive && (exceptId == null || e.Id != exceptId))
            .OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
            .ToListAsync();
        OrgUnits = await db.OrgUnits.Where(u => u.IsActive).OrderBy(u => u.Name).ToListAsync();
    }

    /// <summary>
    /// Nadřízený nesmí být zaměstnanec sám ani nikdo z jeho podřízených (cyklus by
    /// zacyklil vyhodnocení „nadřízený nadřízeného“).
    /// </summary>
    private async Task<string?> ValidateManagerAsync(int employeeId, int? managerId)
    {
        if (managerId is null)
            return null;
        if (managerId == employeeId)
            return "Zaměstnanec nemůže být nadřízeným sám sobě.";
        if (!await db.Employees.AnyAsync(e => e.Id == managerId))
            return "Vybraný nadřízený neexistuje.";

        var current = managerId;
        var visited = new HashSet<int>();
        while (current is { } id && visited.Add(id))
        {
            if (id == employeeId)
                return "Vybraný nadřízený je podřízeným tohoto zaměstnance — vznikl by cyklus.";
            current = await db.Employees.Where(e => e.Id == id).Select(e => e.ManagerId).FirstOrDefaultAsync();
        }

        return null;
    }

    private async Task LoadIdentifiersAsync(int employeeId)
        => Identifiers = await db.EmployeeIdentifiers
            .Where(i => i.EmployeeId == employeeId)
            .OrderBy(i => i.Type).ThenByDescending(i => i.IsActive).ThenBy(i => i.Value)
            .ToListAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Employee.FirstName) || string.IsNullOrWhiteSpace(Employee.LastName))
        {
            ErrorMessage = "Jméno a příjmení jsou povinné.";
            return RedirectToPage(new { id = Employee.Id == 0 ? (int?)null : Employee.Id });
        }

        if (await ValidateManagerAsync(Employee.Id, ManagerId) is { } managerError)
        {
            ErrorMessage = managerError;
            return RedirectToPage(new { id = Employee.Id == 0 ? (int?)null : Employee.Id });
        }

        if (Employee.Id == 0)
        {
            Employee.Source = RecordSource.Manual;
            Employee.ManagerId = ManagerId;
            Employee.ManagerManual = ManagerId is not null && ManagerManual;
            Employee.OrgUnitId = OrgUnitId;
            Employee.OrgUnitManual = OrgUnitId is not null && OrgUnitManual;
            Employee.Rank = Rank;
            Employee.RankManual = Rank != EmployeeRank.Auto;
            db.Employees.Add(Employee);
            await db.SaveChangesAsync();
            await audit.LogAsync(User.Identity?.Name, "employee-created", "Employee",
                Employee.Id.ToString(), Employee.FullName);
        }
        else
        {
            var existing = await db.Employees.FindAsync(Employee.Id);
            if (existing is null)
                return NotFound();

            if (existing.ManagerId != ManagerId || existing.ManagerManual != ManagerManual)
            {
                await audit.LogAsync(User.Identity?.Name, "employee-manager-changed", "Employee",
                    existing.Id.ToString(),
                    $"nadřízený {existing.ManagerId?.ToString() ?? "—"} → {ManagerId?.ToString() ?? "—"}"
                    + (ManagerManual ? " (ručně)" : ""));
            }
            existing.ManagerId = ManagerId;
            // Bez nadřízeného nemá „ručně“ smysl — import by ho jinak nikdy nedoplnil.
            existing.ManagerManual = ManagerId is not null && ManagerManual;

            if (existing.OrgUnitId != OrgUnitId || existing.OrgUnitManual != (OrgUnitId is not null && OrgUnitManual))
            {
                await audit.LogAsync(User.Identity?.Name, "employee-orgunit-changed", "Employee",
                    existing.Id.ToString(), $"úsek {existing.OrgUnitId?.ToString() ?? "—"} → {OrgUnitId?.ToString() ?? "—"}");
            }
            existing.OrgUnitId = OrgUnitId;
            existing.OrgUnitManual = OrgUnitId is not null && OrgUnitManual;

            // Kategorie: „odvodit“ ponechá poslední odvozenou hodnotu a při příštím přepočtu se aktualizuje.
            var rankManual = Rank != EmployeeRank.Auto;
            if (existing.RankManual != rankManual || (rankManual && existing.Rank != Rank))
            {
                await audit.LogAsync(User.Identity?.Name, "employee-rank-changed", "Employee",
                    existing.Id.ToString(), $"kategorie {existing.Rank} → {(rankManual ? Rank.ToString() : "odvodit")}");
            }
            existing.RankManual = rankManual;
            if (rankManual)
                existing.Rank = Rank;

            existing.FirstName = Employee.FirstName;
            existing.LastName = Employee.LastName;
            existing.PersonalNumber = Employee.PersonalNumber;
            existing.Email = Employee.Email;
            existing.Department = Employee.Department;
            existing.AdAccount = Employee.AdAccount;
            existing.WinPakCardHolderId = Employee.WinPakCardHolderId;
            existing.CardNumber = Employee.CardNumber;
            existing.IsActive = Employee.IsActive;
            await db.SaveChangesAsync();
            await audit.LogAsync(User.Identity?.Name, "employee-updated", "Employee",
                existing.Id.ToString(), existing.FullName);
        }

        return RedirectToPage("Index");
    }

    // ---------- Identifikátory ----------

    public async Task<IActionResult> OnPostAddIdentifierAsync(
        int id, string type, string value, DateTime? validFrom, DateTime? validTo, string? note)
    {
        if (!await db.Employees.AnyAsync(e => e.Id == id))
            return NotFound();

        if (string.IsNullOrWhiteSpace(value))
        {
            ErrorMessage = "Zadejte hodnotu identifikátoru.";
            return RedirectToPage(new { id });
        }

        var parsedType = Enum.TryParse<IdentifierType>(type, out var t) ? t : IdentifierType.Other;
        var normalized = EmployeeIdentifier.Normalize(value);

        // Táž karta/SPZ nesmí patřit dvěma lidem — jinak by nešlo dohledat, kdo prošel.
        var owner = await db.EmployeeIdentifiers
            .Include(i => i.Employee)
            .FirstOrDefaultAsync(i => i.Type == parsedType && i.Value == normalized && i.EmployeeId != id);
        if (owner is not null)
        {
            ErrorMessage = $"{TypeName(parsedType)} {normalized} už je evidována u zaměstnance "
                         + $"{owner.Employee?.FullName}.";
            return RedirectToPage(new { id });
        }

        if (await db.EmployeeIdentifiers.AnyAsync(i => i.EmployeeId == id && i.Type == parsedType && i.Value == normalized))
        {
            ErrorMessage = $"{TypeName(parsedType)} {normalized} už je u tohoto zaměstnance evidována.";
            return RedirectToPage(new { id });
        }

        db.EmployeeIdentifiers.Add(new EmployeeIdentifier
        {
            EmployeeId = id,
            Type = parsedType,
            Value = normalized,
            Note = note,
            ValidFrom = validFrom,
            ValidTo = validTo,
            Source = RecordSource.Manual,
        });
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "identifier-added", "Employee", id.ToString(),
            $"{parsedType} {normalized}");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostToggleIdentifierAsync(int id, int identifierId)
    {
        var identifier = await db.EmployeeIdentifiers.FindAsync(identifierId);
        if (identifier is null || identifier.EmployeeId != id)
            return NotFound();

        identifier.IsActive = !identifier.IsActive;
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name,
            identifier.IsActive ? "identifier-activated" : "identifier-deactivated",
            "Employee", id.ToString(), $"{identifier.Type} {identifier.Value}");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteIdentifierAsync(int id, int identifierId)
    {
        var identifier = await db.EmployeeIdentifiers.FindAsync(identifierId);
        if (identifier is not null && identifier.EmployeeId == id)
        {
            db.EmployeeIdentifiers.Remove(identifier);
            await db.SaveChangesAsync();
            await audit.LogAsync(User.Identity?.Name, "identifier-deleted", "Employee", id.ToString(),
                $"{identifier.Type} {identifier.Value}");
        }

        return RedirectToPage(new { id });
    }
}
