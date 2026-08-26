using System.Security.Claims;
using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages;

public class MyAccessModel(AcsDbContext db) : PageModel
{
    public Employee? Employee { get; private set; }
    public List<AccessRequestItem> ActiveItems { get; private set; } = [];
    public List<AccessRequestItem> PendingItems { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await db.Users.Include(u => u.Employee).FirstOrDefaultAsync(u => u.Id == userId);
        Employee = user?.Employee;
        if (Employee is null)
            return;

        var items = await db.AccessRequestItems
            .Include(i => i.Reader).ThenInclude(r => r!.Room).ThenInclude(room => room!.Floor).ThenInclude(f => f!.Building)
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
}
