using System.Security.Claims;
using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Requests;

public class IndexModel(AcsDbContext db, RequestWorkflowService workflow) : PageModel
{
    public List<AccessRequest> MyRequests { get; private set; } = [];
    public List<AccessRequestItem> PendingForMe { get; private set; } = [];

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task OnGetAsync()
    {
        var userId = CurrentUserId;
        MyRequests = await db.AccessRequests
            .Include(r => r.TargetEmployee)
            .Include(r => r.Items)
            .Where(r => r.RequesterUserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(100)
            .ToListAsync();

        PendingForMe = await workflow.GetPendingForApproverAsync(userId, User.IsInRole("Admin"));
    }

    public string Summarize(AccessRequest request)
    {
        var statuses = request.Items.Select(i => i.Status).ToList();
        if (statuses.All(s => s is RequestStatus.PushedToWinPak or RequestStatus.ManuallyConfirmed or RequestStatus.Issued))
            return "dokončeno";
        if (statuses.Any(s => s == RequestStatus.Pending))
            return "čeká na schválení";
        if (statuses.Any(s => s == RequestStatus.Approved))
            return request.Items.All(i => i.IsParking) ? "schváleno — u správce parkování" : "schváleno — u správce karet";
        if (request.Kind == RequestKind.Revoke && statuses.All(s => s == RequestStatus.Revoked))
            return "odebráno";
        if (statuses.All(s => s == RequestStatus.Rejected))
            return "zamítnuto";
        return "částečně vyřízeno";
    }
}
