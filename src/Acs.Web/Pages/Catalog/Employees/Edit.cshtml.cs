using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Acs.Web.Pages.Catalog.Employees;

public class EditModel(AcsDbContext db, AuditService audit) : PageModel
{
    [BindProperty]
    public Employee Employee { get; set; } = new() { FirstName = "", LastName = "" };

    [TempData] public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is null)
            return Page();

        var employee = await db.Employees.FindAsync(id.Value);
        if (employee is null)
            return NotFound();

        Employee = employee;
        return Page();
    }

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
}
