using System.Security.Claims;
using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Security;

/// <summary>Žádost o zřízení / rozšíření kamerového systému nebo EZS (schvaluje vedoucí OVBKŘ, realizuje ICT).</summary>
public class NewModel(AcsDbContext db, RequestWorkflowService workflow, SettingsService settings) : PageModel
{
    public List<Employee> Employees { get; private set; } = [];
    public List<Building> Buildings { get; private set; } = [];
    public List<Floor> Floors { get; private set; } = [];
    public List<Room> Rooms { get; private set; } = [];
    public int? MyEmployeeId { get; private set; }
    public string? MyEmployeeName { get; private set; }
    public bool CanActForOthers { get; private set; }
    public ApprovalMatrix? Matrix { get; private set; }

    [TempData] public string? ErrorMessage { get; set; }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private bool ComputeCanActForOthers()
        => User.IsInRole("Admin") || User.IsInRole("CardAdmin") || User.IsInRole("CatalogManager") || User.IsInRole("IctAdmin");

    public async Task OnGetAsync()
    {
        CanActForOthers = ComputeCanActForOthers();

        var user = await db.Users.Include(u => u.Employee).FirstOrDefaultAsync(u => u.Id == CurrentUserId);
        MyEmployeeId = user?.EmployeeId;
        MyEmployeeName = user?.Employee?.FullName;

        Employees = CanActForOthers
            ? await db.Employees.Where(e => e.IsActive).OrderBy(e => e.LastName).ThenBy(e => e.FirstName).ToListAsync()
            : [];

        Buildings = await db.Buildings.OrderBy(b => b.Name).ToListAsync();
        Floors = await db.Floors.Include(f => f.Building).OrderBy(f => f.Building!.Name).ThenBy(f => f.SortOrder).ToListAsync();
        Rooms = await db.Rooms.Include(r => r.Floor).ThenInclude(f => f!.Building)
            .OrderBy(r => r.Floor!.Building!.Name).ThenBy(r => r.Floor!.SortOrder).ThenBy(r => r.Name).ToListAsync();

        var matrixId = await settings.GetIntAsync(SettingKeys.SecurityMatrixId, 0);
        Matrix = matrixId > 0
            ? await db.ApprovalMatrices.Include(m => m.Levels).FirstOrDefaultAsync(m => m.Id == matrixId)
            : await workflow.GetDefaultMatrixAsync();
    }

    public async Task<IActionResult> OnPostAsync(int? targetEmployeeId, string? kind, string? title, string? description,
        int? buildingId, int? floorId, int? roomId, string? locationText, string? justification)
    {
        if (targetEmployeeId is null)
        {
            ErrorMessage = "Vyberte zaměstnance (žadatele).";
            return RedirectToPage();
        }

        var parsedKind = Enum.TryParse<SecurityRequestKind>(kind, out var k) ? k : SecurityRequestKind.Camera;
        var matrixId = await settings.GetIntAsync(SettingKeys.SecurityMatrixId, 0);
        try
        {
            var request = await workflow.CreateSecurityRequestAsync(
                CurrentUserId, targetEmployeeId.Value,
                new SecurityRequestInput(parsedKind, title ?? "", description, buildingId, floorId, roomId, locationText, justification),
                matrixId > 0 ? matrixId : null,
                requesterCanActForOthers: ComputeCanActForOthers());
            return RedirectToPage("/Requests/Detail", new { id = request.Id });
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
            return RedirectToPage();
        }
    }
}
