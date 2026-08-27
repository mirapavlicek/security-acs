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
        if (id is null)
            return Page();

        var employee = await db.Employees.FindAsync(id.Value);
        if (employee is null)
            return NotFound();

        Employee = employee;
        await LoadIdentifiersAsync(id.Value);
        return Page();
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

        if (Employee.Id == 0)
        {
            Employee.Source = RecordSource.Manual;
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
