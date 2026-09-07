using System.Security.Claims;
using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Parking;

/// <summary>Detail parkovacího povolení: parametry, průběh schvalování, tisk.</summary>
public class PermitModel(AcsDbContext db, ParkingAdminService parkingAdmin, RequestWorkflowService workflow) : PageModel
{
    public AccessRequestItem Item { get; private set; } = null!;
    public ParkingPermit Permit => Item.ParkingPermit!;
    public List<AccessRequestItem> RevokeItems { get; private set; } = [];
    public bool CanPrint { get; private set; }

    /// <summary>Správce parkování může měnit vyhrazené místo u vydaného povolení.</summary>
    public bool CanAssignSpot { get; private set; }

    /// <summary>Aktivní místa v areálech povolení (pro výběr).</summary>
    public List<ParkingSpot> AvailableSpots { get; private set; } = [];

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var item = await parkingAdmin.GetGrantItemAsync(id);
        if (item is null)
            return NotFound();

        var isAdmin = User.IsInRole("Admin") || User.IsInRole("ParkingAdmin");
        var myEmployeeId = await db.Users.Where(u => u.Id == CurrentUserId)
            .Select(u => u.EmployeeId).FirstOrDefaultAsync();
        var canDecide = (await workflow.GetPendingForApproverAsync(CurrentUserId, User.IsInRole("Admin")))
            .Any(i => i.Id == item.Id);
        var canView = isAdmin
            || item.Request!.RequesterUserId == CurrentUserId
            || (myEmployeeId is not null && item.Request.TargetEmployeeId == myEmployeeId)
            || canDecide;
        if (!canView)
            return Forbid();

        Item = item;
        RevokeItems = await db.AccessRequestItems
            .Include(i => i.Request!).ThenInclude(r => r.RequesterUser)
            .Where(i => i.ParkingPermitId == id && i.Request!.Kind == RequestKind.Revoke)
            .OrderByDescending(i => i.Request!.CreatedAt)
            .ToListAsync();
        CanPrint = item.Status == RequestStatus.Issued && (Permit.PermitType?.PrintsWindshieldCard ?? false);
        CanAssignSpot = isAdmin && item.Status == RequestStatus.Issued;
        if (CanAssignSpot)
        {
            var siteIds = Permit.Sites.Select(s => s.SiteId).ToList();
            AvailableSpots = await db.ParkingSpots.Include(s => s.Site)
                .Where(s => s.IsActive && (Permit.AllSites || siteIds.Contains(s.SiteId)))
                .OrderBy(s => s.Site!.SortOrder).ThenBy(s => s.Site!.Name).ThenBy(s => s.SortOrder).ThenBy(s => s.Code)
                .ToListAsync();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAssignSpotAsync(int id, int? parkingSpotId)
    {
        if (!(User.IsInRole("Admin") || User.IsInRole("ParkingAdmin")))
            return Forbid();

        try
        {
            await parkingAdmin.AssignSpotAsync(id, parkingSpotId, User.Identity?.Name);
            Message = parkingSpotId is null
                ? "Vyhrazené místo odebráno — ceduli na původním místě vytiskněte znovu."
                : "Vyhrazené místo přiřazeno — vytiskněte aktuální ceduli na místo.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage(new { id });
    }
}
