using System.Security.Claims;
using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages;

public class MyAccessModel(AcsDbContext db, RequestWorkflowService workflow) : PageModel
{
    public Employee? Employee { get; private set; }
    public List<AccessRequestItem> ActiveItems { get; private set; } = [];
    public List<AccessRequestItem> PendingItems { get; private set; } = [];

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task OnGetAsync()
    {
        var user = await db.Users.Include(u => u.Employee).FirstOrDefaultAsync(u => u.Id == CurrentUserId);
        Employee = user?.Employee;
        if (Employee is null)
            return;

        var items = await db.AccessRequestItems
            .Include(i => i.Reader).ThenInclude(r => r!.Room).ThenInclude(room => room!.Floor).ThenInclude(f => f!.Building)
            .Include(i => i.Reader).ThenInclude(r => r!.Room).ThenInclude(room => room!.Corridor)
            .Include(i => i.Reader).ThenInclude(r => r!.Corridor).ThenInclude(c => c!.Floor).ThenInclude(f => f!.Building)
            .Include(i => i.ReaderGroup)
            .Where(i => i.Request!.TargetEmployeeId == Employee.Id && i.Request.Kind == RequestKind.Grant)
            .ToListAsync();

        ActiveItems = items
            .Where(i => i.Status is RequestStatus.PushedToWinPak or RequestStatus.ManuallyConfirmed)
            .OrderBy(i => i.Reader?.Name)
            .ToList();
        PendingItems = items
            .Where(i => i.Status is RequestStatus.Pending or RequestStatus.Approved)
            .OrderBy(i => i.Reader?.Name)
            .ToList();
    }

    public async Task<IActionResult> OnPostRevokeAsync(int? readerId, int? groupId)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == CurrentUserId);
        if (user?.EmployeeId is null)
            return Forbid();

        try
        {
            var request = await workflow.CreateRequestAsync(
                CurrentUserId, user.EmployeeId.Value,
                readerId is null ? [] : [readerId.Value],
                "žádost o odebrání vlastního přístupu", RequestKind.Revoke,
                groupIds: groupId is null ? [] : [groupId.Value]);
            Message = $"Žádost o odebrání podána (#{request.Id}).";
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }
}
