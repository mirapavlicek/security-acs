using System.Security.Claims;
using System.Text.Json;
using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages;

public class PlansModel(AcsDbContext db, Acs.Infrastructure.Workflow.ReaderGroupService groups) : PageModel
{
    public List<Floor> FloorsWithSchema { get; private set; } = [];
    public Floor? SelectedFloor { get; private set; }
    public bool HasUnderlay { get; private set; }
    public string LayoutJson { get; private set; } = "{}";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task OnGetAsync(int? floorId)
    {
        // Patra s podkladem NEBO s rozmístěnými prvky (interaktivní plán funguje i bez obrázku).
        FloorsWithSchema = await db.Floors
            .Include(f => f.Building)
            .Include(f => f.Section)
            .Where(f => f.SchemaImage != null
                        || f.Rooms.Any(r => r.PlanX != null)
                        || f.Corridors.Any(c => c.Readers.Any(r => r.SchemaX != null))
                        || f.Rooms.Any(r => r.Readers.Any(x => x.SchemaX != null)))
            .OrderBy(f => f.Building!.Name).ThenBy(f => f.SortOrder)
            .ToListAsync();

        SelectedFloor = FloorsWithSchema.FirstOrDefault(f => f.Id == floorId)
            ?? (floorId is null ? null : FloorsWithSchema.FirstOrDefault());
        if (SelectedFloor is null)
            return;

        HasUnderlay = SelectedFloor.SchemaImage is not null;

        var rooms = await db.Rooms
            .Where(r => r.FloorId == SelectedFloor.Id && r.PlanX != null)
            .Select(r => new { r.Id, r.Name, x = r.PlanX, y = r.PlanY, w = r.PlanW, h = r.PlanH })
            .ToListAsync();
        var readers = await db.Readers
            .Where(r => ((r.Room != null && r.Room.FloorId == SelectedFloor.Id)
                         || (r.Corridor != null && r.Corridor.FloorId == SelectedFloor.Id))
                        && r.SchemaX != null)
            .Select(r => new { r.Id, r.Name, x = r.SchemaX, y = r.SchemaY, r.RoomId })
            .ToListAsync();
        var devices = await db.PlanDevices
            .Where(d => d.FloorId == SelectedFloor.Id)
            .Select(d => new { type = d.Type.ToString(), d.Name, d.X, d.Y })
            .ToListAsync();

        // Čtečky, kam má přihlášený aktivní přístup (včetně expandovaných skupin).
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var employeeId = await db.Users.Where(u => u.Id == userId)
            .Select(u => u.EmployeeId).FirstOrDefaultAsync();
        var myReaderIds = new HashSet<int>();
        if (employeeId is not null)
        {
            var activeItems = await db.AccessRequestItems
                .Where(i => i.Request!.TargetEmployeeId == employeeId
                            && i.Request.Kind == RequestKind.Grant
                            && (i.Status == RequestStatus.PushedToWinPak
                                || i.Status == RequestStatus.ManuallyConfirmed))
                .Select(i => new { i.ReaderId, i.ReaderGroupId })
                .ToListAsync();
            myReaderIds = activeItems.Where(i => i.ReaderId != null).Select(i => i.ReaderId!.Value).ToHashSet();
            var groupIds = activeItems.Where(i => i.ReaderGroupId != null).Select(i => i.ReaderGroupId!.Value).ToList();
            if (groupIds.Count > 0)
                myReaderIds.UnionWith(await groups.ExpandReaderIdsAsync(groupIds));
        }

        // Do místnosti se člověk dostane, když má přístup na některou její čtečku —
        // na plánu se pak rovnou zvýrazní, takže je vidět, kam smí, bez klikání.
        var myRoomIds = readers
            .Where(r => r.RoomId != null && myReaderIds.Contains(r.Id))
            .Select(r => r.RoomId!.Value)
            .ToHashSet();

        LayoutJson = JsonSerializer.Serialize(new
        {
            rooms = rooms.Select(r => new { r.Id, r.Name, r.x, r.y, r.w, r.h, mine = myRoomIds.Contains(r.Id) }),
            readers = readers.Select(r => new { r.Id, r.Name, r.x, r.y, mine = myReaderIds.Contains(r.Id) }),
            devices,
        }, JsonOpts);
    }
}
