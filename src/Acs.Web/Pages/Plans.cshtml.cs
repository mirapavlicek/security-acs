using System.Security.Claims;
using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages;

public class PlansModel(AcsDbContext db) : PageModel
{
    public List<Floor> FloorsWithSchema { get; private set; } = [];
    public Floor? SelectedFloor { get; private set; }
    public List<Reader> Readers { get; private set; } = [];
    public HashSet<int> MyReaderIds { get; private set; } = [];

    public async Task OnGetAsync(int? floorId)
    {
        FloorsWithSchema = await db.Floors
            .Include(f => f.Building)
            .Where(f => f.SchemaImage != null)
            .OrderBy(f => f.Building!.Name).ThenBy(f => f.SortOrder)
            .ToListAsync();

        SelectedFloor = FloorsWithSchema.FirstOrDefault(f => f.Id == floorId)
            ?? (floorId is null ? null : FloorsWithSchema.FirstOrDefault());
        if (SelectedFloor is null)
            return;

        Readers = await db.Readers
            .Where(r => r.Room != null && r.Room.FloorId == SelectedFloor.Id)
            .ToListAsync();

        // Čtečky, ke kterým má přihlášený uživatel (jako zaměstnanec) aktivní přístup.
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var employeeId = await db.Users.Where(u => u.Id == userId)
            .Select(u => u.EmployeeId).FirstOrDefaultAsync();
        if (employeeId is null)
            return;

        MyReaderIds = (await db.AccessRequestItems
                .Where(i => i.Request!.TargetEmployeeId == employeeId
                            && i.Request.Kind == RequestKind.Grant
                            && (i.Status == RequestStatus.PushedToWinPak
                                || i.Status == RequestStatus.ManuallyConfirmed))
                .Select(i => i.ReaderId)
                .ToListAsync())
            .ToHashSet();
    }
}
