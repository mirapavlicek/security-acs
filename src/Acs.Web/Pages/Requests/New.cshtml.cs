using System.Security.Claims;
using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Requests;

public class NewModel(AcsDbContext db, RequestWorkflowService workflow) : PageModel
{
    public List<Employee> Employees { get; private set; } = [];
    public List<Reader> Readers { get; private set; } = [];
    public int? MyEmployeeId { get; private set; }
    public bool CanActForOthers { get; private set; }

    [TempData] public string? ErrorMessage { get; set; }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task OnGetAsync()
    {
        CanActForOthers = User.IsInRole("Admin") || User.IsInRole("CardAdmin") || User.IsInRole("CatalogManager");

        var user = await db.Users.FindAsync(CurrentUserId);
        MyEmployeeId = user?.EmployeeId;

        // Běžný uživatel vidí v nabídce jen sebe; oprávnění i všechny ostatní.
        Employees = CanActForOthers
            ? await db.Employees.Where(e => e.IsActive)
                .OrderBy(e => e.LastName).ThenBy(e => e.FirstName).ToListAsync()
            : await db.Employees.Where(e => e.IsActive && e.Id == MyEmployeeId)
                .ToListAsync();

        Readers = await db.Readers.Where(r => r.IsActive)
            .Include(r => r.Room).ThenInclude(room => room!.Floor).ThenInclude(f => f!.Building)
            .Include(r => r.Dependencies).ThenInclude(d => d.RequiresReader)
            .OrderBy(r => r.Name).ToListAsync();
    }

    public async Task<IActionResult> OnPostAsync(int? targetEmployeeId, int[] readerIds, string? justification)
    {
        if (targetEmployeeId is null || readerIds.Length == 0)
        {
            ErrorMessage = "Vyberte zaměstnance a alespoň jednu čtečku.";
            return RedirectToPage();
        }

        var canActForOthers = User.IsInRole("Admin") || User.IsInRole("CardAdmin") || User.IsInRole("CatalogManager");
        try
        {
            var request = await workflow.CreateRequestAsync(
                CurrentUserId, targetEmployeeId.Value, readerIds, justification,
                requesterCanActForOthers: canActForOthers);
            return RedirectToPage("Detail", new { id = request.Id });
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
            return RedirectToPage();
        }
    }
}
