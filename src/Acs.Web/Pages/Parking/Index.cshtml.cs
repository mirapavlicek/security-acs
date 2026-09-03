using System.Security.Claims;
using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Parking;

public class IndexModel(AcsDbContext db, RequestWorkflowService workflow) : PageModel
{
    public Employee? Employee { get; private set; }
    public List<AccessRequestItem> ActiveItems { get; private set; } = [];
    public List<AccessRequestItem> PendingItems { get; private set; } = [];
    public List<AccessRequestItem> HistoryItems { get; private set; } = [];
    public HashSet<int> RevokeRequested { get; private set; } = [];
    public bool HasTypes { get; private set; }

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task OnGetAsync()
    {
        HasTypes = await db.ParkingPermitTypes.AnyAsync(t => t.IsActive);

        var user = await db.Users.Include(u => u.Employee).FirstOrDefaultAsync(u => u.Id == CurrentUserId);
        Employee = user?.Employee;
        if (Employee is null)
            return;

        var items = await db.AccessRequestItems
            .Include(i => i.Request)
            .Include(i => i.Stages)
            .Include(i => i.ParkingPermit!).ThenInclude(p => p.PermitType)
            .Include(i => i.ParkingPermit!).ThenInclude(p => p.Plates)
            .Include(i => i.ParkingPermit!).ThenInclude(p => p.Sites).ThenInclude(s => s.Site)
            .Where(i => i.ParkingPermitId != null
                        && i.Request!.TargetEmployeeId == Employee.Id
                        && i.Request.Kind == RequestKind.Grant)
            .OrderByDescending(i => i.Request!.CreatedAt)
            .ToListAsync();

        ActiveItems = items.Where(i => i.Status == RequestStatus.Issued).ToList();
        PendingItems = items.Where(i => i.Status is RequestStatus.Pending or RequestStatus.Approved).ToList();
        HistoryItems = items.Where(i => i.Status is RequestStatus.Rejected or RequestStatus.Revoked or RequestStatus.Cancelled).ToList();

        var activePermitIds = ActiveItems.Select(i => i.ParkingPermitId!.Value).ToList();
        RevokeRequested = (await db.AccessRequestItems
            .Where(i => i.ParkingPermitId != null && activePermitIds.Contains(i.ParkingPermitId.Value)
                        && i.Request!.Kind == RequestKind.Revoke
                        && (i.Status == RequestStatus.Pending || i.Status == RequestStatus.Approved))
            .Select(i => i.ParkingPermitId!.Value)
            .ToListAsync()).ToHashSet();
    }

    public async Task<IActionResult> OnPostRevokeAsync(int permitId)
    {
        try
        {
            var request = await workflow.CreateParkingRevokeRequestAsync(
                CurrentUserId, permitId, "žádost o odebrání vlastního parkovacího povolení");
            Message = $"Žádost o odebrání podána (#{request.Id}) — vyřídí ji správce parkování.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }
}
