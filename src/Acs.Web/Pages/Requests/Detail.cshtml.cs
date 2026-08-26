using System.Security.Claims;
using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Requests;

public class DetailModel(AcsDbContext db, RequestWorkflowService workflow) : PageModel
{
    public AccessRequest Request { get; private set; } = null!;
    public HashSet<int> CanDecide { get; private set; } = [];

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var request = await db.AccessRequests
            .Include(r => r.TargetEmployee)
            .Include(r => r.RequesterUser)
            .Include(r => r.Items).ThenInclude(i => i.Reader)
            .Include(r => r.Items).ThenInclude(i => i.Decisions).ThenInclude(d => d.ApproverUser)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (request is null)
            return NotFound();

        Request = request;
        var pending = await workflow.GetPendingForApproverAsync(CurrentUserId);
        CanDecide = pending.Where(i => i.RequestId == id).Select(i => i.Id).ToHashSet();
        return Page();
    }

    public Task<IActionResult> OnPostApproveAsync(int id, int itemId, string? comment)
        => DecideAsync(id, itemId, true, comment);

    public Task<IActionResult> OnPostRejectAsync(int id, int itemId, string? comment)
        => DecideAsync(id, itemId, false, comment);

    private async Task<IActionResult> DecideAsync(int id, int itemId, bool approve, string? comment)
    {
        if (!approve && string.IsNullOrWhiteSpace(comment))
        {
            ErrorMessage = "U zamítnutí je komentář povinný.";
            return RedirectToPage(new { id });
        }

        try
        {
            await workflow.DecideAsync(itemId, CurrentUserId, approve, comment);
            Message = approve ? "Položka schválena." : "Položka zamítnuta.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage(new { id });
    }
}
