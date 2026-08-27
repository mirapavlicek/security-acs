using System.Text.Json;
using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog;

/// <summary>Interaktivní editor plánu patra: místnosti (obdélníky), čtečky a zařízení.</summary>
public class FloorPlanModel(AcsDbContext db, AuditService audit) : PageModel
{
    public Floor Floor { get; private set; } = null!;
    public bool HasUnderlay { get; private set; }
    public string LayoutJson { get; private set; } = "{}";

    [TempData] public string? Message { get; set; }

    public record RoomDto(int Id, string Name, double? X, double? Y, double? W, double? H);
    public record ReaderDto(int Id, string Name, double? X, double? Y);
    public record DeviceDto(int? Id, string Type, string Name, double X, double Y);
    public record LayoutDto(List<RoomDto> Rooms, List<ReaderDto> Readers, List<DeviceDto> Devices);

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var floor = await db.Floors.Include(f => f.Building).FirstOrDefaultAsync(f => f.Id == id);
        if (floor is null)
            return NotFound();

        Floor = floor;
        HasUnderlay = floor.SchemaImage is not null;
        LayoutJson = JsonSerializer.Serialize(await LoadLayoutAsync(id), JsonOpts);
        return Page();
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private async Task<LayoutDto> LoadLayoutAsync(int floorId)
    {
        var rooms = await db.Rooms.Where(r => r.FloorId == floorId)
            .OrderBy(r => r.Name)
            .Select(r => new RoomDto(r.Id, r.Name, r.PlanX, r.PlanY, r.PlanW, r.PlanH))
            .ToListAsync();
        var readers = await db.Readers
            .Where(r => (r.Room != null && r.Room.FloorId == floorId)
                        || (r.Corridor != null && r.Corridor.FloorId == floorId))
            .OrderBy(r => r.Name)
            .Select(r => new ReaderDto(r.Id, r.Name, r.SchemaX, r.SchemaY))
            .ToListAsync();
        var devices = await db.PlanDevices.Where(d => d.FloorId == floorId)
            .Select(d => new DeviceDto(d.Id, d.Type.ToString(), d.Name, d.X, d.Y))
            .ToListAsync();
        return new LayoutDto(rooms, readers, devices);
    }

    /// <summary>Uloží celé rozložení plánu (JSON z editoru).</summary>
    public async Task<IActionResult> OnPostSaveLayoutAsync(int id, [FromBody] LayoutDto layout)
    {
        var floorExists = await db.Floors.AnyAsync(f => f.Id == id);
        if (!floorExists)
            return NotFound();

        var roomIds = layout.Rooms.Select(r => r.Id).ToList();
        var rooms = await db.Rooms.Where(r => r.FloorId == id && roomIds.Contains(r.Id)).ToListAsync();
        foreach (var room in rooms)
        {
            var dto = layout.Rooms.First(r => r.Id == room.Id);
            room.PlanX = Clamp(dto.X);
            room.PlanY = Clamp(dto.Y);
            room.PlanW = dto.W is null ? null : Math.Clamp(dto.W.Value, 1, 100);
            room.PlanH = dto.H is null ? null : Math.Clamp(dto.H.Value, 1, 100);
        }

        var readerIds = layout.Readers.Select(r => r.Id).ToList();
        var readers = await db.Readers.Where(r => readerIds.Contains(r.Id)).ToListAsync();
        foreach (var reader in readers)
        {
            var dto = layout.Readers.First(r => r.Id == reader.Id);
            reader.SchemaX = Clamp(dto.X);
            reader.SchemaY = Clamp(dto.Y);
        }

        // Zařízení: upsert podle Id, ostatní na patře smazat.
        var existing = await db.PlanDevices.Where(d => d.FloorId == id).ToListAsync();
        var keptIds = new HashSet<int>();
        foreach (var dto in layout.Devices)
        {
            if (!Enum.TryParse<PlanDeviceType>(dto.Type, ignoreCase: true, out var type))
                continue;

            var device = dto.Id is not null ? existing.FirstOrDefault(d => d.Id == dto.Id) : null;
            if (device is null)
            {
                device = new PlanDevice { FloorId = id, Type = type, Name = dto.Name, X = 0, Y = 0 };
                db.PlanDevices.Add(device);
            }

            device.Type = type;
            device.Name = string.IsNullOrWhiteSpace(dto.Name) ? type.ToString() : dto.Name.Trim();
            device.X = Clamp(dto.X) ?? 50;
            device.Y = Clamp(dto.Y) ?? 50;
            if (device.Id != 0)
                keptIds.Add(device.Id);
        }

        db.PlanDevices.RemoveRange(existing.Where(d => !keptIds.Contains(d.Id)));

        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "floor-plan-saved", "Floor", id.ToString(),
            $"místností {layout.Rooms.Count}, čteček {layout.Readers.Count}, zařízení {layout.Devices.Count}");
        return new JsonResult(new { ok = true });
    }

    private static double? Clamp(double? value)
        => value is null ? null : Math.Clamp(value.Value, 0, 100);
}
