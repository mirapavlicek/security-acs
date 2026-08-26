using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Sync;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog.Employees;

public class IndexModel(AcsDbContext db, EmployeeSyncService employeeSync) : PageModel
{
    public List<Employee> Employees { get; private set; } = [];

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        var query = db.Employees.AsQueryable();
        if (!string.IsNullOrWhiteSpace(Search))
        {
            query = query.Where(e =>
                e.FirstName.Contains(Search)
                || e.LastName.Contains(Search)
                || (e.Department != null && e.Department.Contains(Search))
                || (e.PersonalNumber != null && e.PersonalNumber.Contains(Search))
                || (e.CardNumber != null && e.CardNumber.Contains(Search))
                || (e.AdAccount != null && e.AdAccount.Contains(Search)));
        }

        Employees = await query.OrderBy(e => e.LastName).ThenBy(e => e.FirstName).Take(500).ToListAsync();
    }

    public async Task<IActionResult> OnPostSyncAsync()
    {
        try
        {
            var result = await employeeSync.SyncAsync(User.Identity?.Name);
            Message = $"Synchronizace dokončena: {result}.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Synchronizace selhala: {ex.Message}";
        }

        return RedirectToPage();
    }
}
