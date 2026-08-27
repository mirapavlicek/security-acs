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
    public List<ReaderGroup> Groups { get; private set; } = [];
    public int? MyEmployeeId { get; private set; }
    public string? MyEmployeeName { get; private set; }
    public bool CanActForOthers { get; private set; }

    [TempData] public string? ErrorMessage { get; set; }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task OnGetAsync()
    {
        CanActForOthers = User.IsInRole("Admin") || User.IsInRole("CardAdmin") || User.IsInRole("CatalogManager");

        var user = await db.Users.Include(u => u.Employee).FirstOrDefaultAsync(u => u.Id == CurrentUserId);
        MyEmployeeId = user?.EmployeeId;
        MyEmployeeName = user?.Employee?.FullName;

        // Výběr zaměstnance se zobrazuje jen oprávněným; běžný uživatel žádá sám za sebe.
        Employees = CanActForOthers
            ? await db.Employees.Where(e => e.IsActive)
                .OrderBy(e => e.LastName).ThenBy(e => e.FirstName).ToListAsync()
            : [];

        Readers = await db.Readers.Where(r => r.IsActive)
            .Include(r => r.Room).ThenInclude(room => room!.Floor).ThenInclude(f => f!.Building)
            .Include(r => r.Room).ThenInclude(room => room!.Corridor)
            .Include(r => r.Corridor).ThenInclude(c => c!.Floor).ThenInclude(f => f!.Building)
            .Include(r => r.Dependencies).ThenInclude(d => d.RequiresReader)
            .OrderBy(r => r.Name).ToListAsync();

        Groups = await db.ReaderGroups.Where(g => g.IsActive).OrderBy(g => g.Name).ToListAsync();
    }

    public async Task<IActionResult> OnPostAsync(int? targetEmployeeId, int[] readerIds, int[] groupIds, string? justification)
    {
        if (targetEmployeeId is null || (readerIds.Length == 0 && groupIds.Length == 0))
        {
            ErrorMessage = "Vyberte zaměstnance a alespoň jednu čtečku nebo skupinu.";
            return RedirectToPage();
        }

        var canActForOthers = User.IsInRole("Admin") || User.IsInRole("CardAdmin") || User.IsInRole("CatalogManager");
        try
        {
            var request = await workflow.CreateRequestAsync(
                CurrentUserId, targetEmployeeId.Value, readerIds, justification,
                requesterCanActForOthers: canActForOthers, groupIds: groupIds);
            return RedirectToPage("Detail", new { id = request.Id });
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
            return RedirectToPage();
        }
    }
}
