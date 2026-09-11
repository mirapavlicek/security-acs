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

    /// <summary>Všechny žádosti (kontrola OBP / administrátor) — poslední.</summary>
    public List<AccessRequest> AllRequests { get; private set; } = [];
    public bool CanSeeAll => User.IsInRole("Admin") || User.IsInRole("Auditor");

    /// <summary>Proč položka čeká právě na mě: „nadřízený“, „rozhodne správce“ (bez schvalovatele) nebo nic.</summary>
    public Dictionary<int, LevelResolution> Resolutions { get; private set; } = [];

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
        Resolutions = await workflow.ResolveCurrentLevelsAsync(PendingForMe);

        if (CanSeeAll)
        {
            AllRequests = await db.AccessRequests
                .Include(r => r.TargetEmployee)
                .Include(r => r.RequesterUser)
                .Include(r => r.Items)
                .OrderByDescending(r => r.CreatedAt)
                .Take(200)
                .ToListAsync();
        }
    }

    /// <summary>Krátký popisek role u položky ve frontě („jako nadřízený“ / „rozhodne správce“).</summary>
    public string? RoleHint(AccessRequestItem item)
    {
        if (!Resolutions.TryGetValue(item.Id, out var resolution))
            return null;
        if (resolution.RequiresAdminFallback)
            return "bez schvalovatele — rozhodne správce";
        var dynamic = resolution.Approvers.Where(a => a.IsDynamic).ToList();
        return dynamic.Count == 0
            ? null
            : string.Join(", ", dynamic.Select(m => $"{m.OriginLabel}: {m.DisplayName}"));
    }

    public string Summarize(AccessRequest request)
    {
        var statuses = request.Items.Select(i => i.Status).ToList();
        if (statuses.All(s => s is RequestStatus.PushedToWinPak or RequestStatus.ManuallyConfirmed or RequestStatus.Issued))
            return "dokončeno";
        if (statuses.Any(s => s == RequestStatus.Pending))
            return "čeká na schválení";
        if (statuses.Any(s => s == RequestStatus.Approved))
            return request.Items.All(i => i.IsParking) ? "schváleno — u správce parkování"
                : request.Items.All(i => i.IsSecurity) ? "schváleno — čeká na realizaci ICT"
                : "schváleno — u správce karet";
        if (request.Kind == RequestKind.Revoke && statuses.All(s => s == RequestStatus.Revoked))
            return "odebráno";
        if (statuses.All(s => s == RequestStatus.Rejected))
            return "zamítnuto";
        return "částečně vyřízeno";
    }
}
