using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Plans;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog;

/// <summary>Souhrn budovy pro sbalený seznam (jen počty, žádná podřízená data).</summary>
public record BuildingSummary(Building Building, int Sections, int Floors, int Corridors, int Rooms, int Readers);

/// <summary>Obsah jedné budovy — části a patra s počty (dotahuje se na rozbalení).</summary>
public record BuildingContent(
    Building Building,
    List<BuildingSection> Sections,
    List<Floor> Floors,
    Dictionary<int, (int Rooms, int Corridors, int Readers)> FloorCounts);

/// <summary>Obsah jednoho patra — chodby a místnosti (dotahuje se na rozbalení).</summary>
public record FloorContent(
    Floor Floor,
    List<Corridor> Corridors,
    List<Room> Rooms,
    List<Corridor> AllCorridors,
    Dictionary<int, int> CorridorReaderCounts,
    Dictionary<int, int> RoomReaderCounts);

public class PlacesModel(AcsDbContext db, AuditService audit, PlanGenerationService planGenerator) : PageModel
{
    public List<BuildingSummary> Buildings { get; private set; } = [];

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    /// <summary>Uzel, který se má po přesměrování znovu rozbalit (např. „floor-12“).</summary>
    [BindProperty(SupportsGet = true)]
    public string? Open { get; set; }

    /// <summary>Nadřazený uzel, který se musí rozbalit jako první, aby se <see cref="Open"/> vůbec načetl.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Parent { get; set; }

    /// <summary>Na úvod jen budovy a souhrnné počty — nic víc se nenačítá.</summary>
    public async Task OnGetAsync()
    {
        var buildings = await db.Buildings.OrderBy(b => b.Name).ToListAsync();
        Buildings = [];
        foreach (var building in buildings)
        {
            var floorIds = await db.Floors.Where(f => f.BuildingId == building.Id)
                .Select(f => f.Id).ToListAsync();
            Buildings.Add(new BuildingSummary(
                building,
                Sections: await db.BuildingSections.CountAsync(s => s.BuildingId == building.Id),
                Floors: floorIds.Count,
                Corridors: await db.Corridors.CountAsync(c => floorIds.Contains(c.FloorId)),
                Rooms: await db.Rooms.CountAsync(r => floorIds.Contains(r.FloorId)),
                Readers: await db.Readers.CountAsync(r =>
                    (r.Room != null && floorIds.Contains(r.Room.FloorId))
                    || (r.Corridor != null && floorIds.Contains(r.Corridor.FloorId)))));
        }
    }

    // ---------- Dotahování na kliknutí ----------

    public async Task<IActionResult> OnGetBuildingAsync(int id)
    {
        var building = await db.Buildings.FirstOrDefaultAsync(b => b.Id == id);
        if (building is null)
            return NotFound();

        var sections = await db.BuildingSections.Where(s => s.BuildingId == id)
            .OrderBy(s => s.SortOrder).ToListAsync();
        var floors = await db.Floors.Include(f => f.Section)
            .Where(f => f.BuildingId == id)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Name)
            .ToListAsync();

        var floorIds = floors.Select(f => f.Id).ToList();
        var rooms = await db.Rooms.Where(r => floorIds.Contains(r.FloorId))
            .GroupBy(r => r.FloorId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
        var corridors = await db.Corridors.Where(c => floorIds.Contains(c.FloorId))
            .GroupBy(c => c.FloorId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
        var readers = await db.Readers
            .Where(r => (r.Room != null && floorIds.Contains(r.Room.FloorId))
                        || (r.Corridor != null && floorIds.Contains(r.Corridor.FloorId)))
            .Select(r => r.Room != null ? r.Room.FloorId : r.Corridor!.FloorId)
            .ToListAsync();
        var readerCounts = readers.GroupBy(f => f).ToDictionary(g => g.Key, g => g.Count());

        var counts = floors.ToDictionary(f => f.Id, f => (
            Rooms: rooms.GetValueOrDefault(f.Id),
            Corridors: corridors.GetValueOrDefault(f.Id),
            Readers: readerCounts.GetValueOrDefault(f.Id)));

        return Partial("_BuildingContent", new BuildingContent(building, sections, floors, counts));
    }

    public async Task<IActionResult> OnGetFloorAsync(int id)
    {
        var floor = await db.Floors.Include(f => f.Building).Include(f => f.Section)
            .FirstOrDefaultAsync(f => f.Id == id);
        if (floor is null)
            return NotFound();

        var corridors = await db.Corridors.Include(c => c.ParentCorridor)
            .Where(c => c.FloorId == id).OrderBy(c => c.Name).ToListAsync();
        var rooms = await db.Rooms.Include(r => r.Corridor)
            .Where(r => r.FloorId == id).OrderBy(r => r.Name).ToListAsync();

        var corridorReaders = await db.Readers.Where(r => r.CorridorId != null && r.Corridor!.FloorId == id)
            .GroupBy(r => r.CorridorId!.Value).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
        var roomReaders = await db.Readers.Where(r => r.RoomId != null && r.Room!.FloorId == id)
            .GroupBy(r => r.RoomId!.Value).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        // Nadřazená chodba může být i z jiného patra (řetěz napříč budovou).
        var allCorridors = await db.Corridors.Include(c => c.Floor)
            .OrderBy(c => c.Floor!.SortOrder).ThenBy(c => c.Name).ToListAsync();

        return Partial("_FloorContent",
            new FloorContent(floor, corridors, rooms, allCorridors, corridorReaders, roomReaders));
    }

    /// <summary>Vrátí na seznam s rozbaleným patrem (a budovou nad ním).</summary>
    private async Task<IActionResult> RedirectToFloorAsync(int floorId)
    {
        var buildingId = await db.Floors.Where(f => f.Id == floorId)
            .Select(f => (int?)f.BuildingId).FirstOrDefaultAsync();
        return RedirectToPage(new
        {
            open = $"floor-{floorId}",
            parent = buildingId is null ? null : $"building-{buildingId}",
        });
    }

    // ---------- Budovy ----------

    public async Task<IActionResult> OnPostAddBuildingAsync(string name)
    {
        db.Buildings.Add(new Building { Name = name.Trim() });
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "building-created", "Building", null, name);
        Message = $"Budova {name} přidána.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteBuildingAsync(int buildingId)
    {
        var building = await db.Buildings.FirstOrDefaultAsync(b => b.Id == buildingId);
        if (building is null)
            return NotFound();

        var hasReaders = await db.Readers.AnyAsync(r =>
            (r.Room != null && r.Room.Floor!.BuildingId == buildingId)
            || (r.Corridor != null && r.Corridor.Floor!.BuildingId == buildingId));
        if (hasReaders)
        {
            ErrorMessage = "Budovu nelze smazat — obsahuje místnosti nebo chodby s přiřazenými čtečkami.";
            return RedirectToPage();
        }

        db.Buildings.Remove(building);
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "building-deleted", "Building", buildingId.ToString(), building.Name);
        Message = $"Budova {building.Name} smazána.";
        return RedirectToPage();
    }

    /// <summary>Vygeneruje plány všech pater budovy — po importu z výkresů to udělá práci za správce.</summary>
    public async Task<IActionResult> OnPostGeneratePlansAsync(int buildingId, bool onlyEmpty)
    {
        try
        {
            var result = await planGenerator.GenerateBuildingAsync(buildingId, onlyEmpty, User.Identity?.Name);
            Message = result.Floors.Count switch
            {
                0 => "Budova nemá žádná patra, plány není z čeho sestavit.",
                _ when result.NothingToDo =>
                    $"Doplňovat nebylo co — {result}. Přerovnat plány jde tlačítkem „Generuj všechny znovu“.",
                _ => $"Plány vygenerovány — {result}.",
            };
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Generování plánů se nezdařilo: {ex.Message}";
        }

        return RedirectToPage(new { open = $"building-{buildingId}" });
    }

    // ---------- Části ----------

    public async Task<IActionResult> OnPostAddSectionAsync(int buildingId, string name)
    {
        var maxOrder = await db.BuildingSections.Where(s => s.BuildingId == buildingId)
            .MaxAsync(s => (int?)s.SortOrder) ?? 0;
        db.BuildingSections.Add(new BuildingSection
        {
            BuildingId = buildingId, Name = name.Trim(), SortOrder = maxOrder + 1,
        });
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "section-created", "BuildingSection", null, name);
        return RedirectToPage(new { open = $"building-{buildingId}" });
    }

    public async Task<IActionResult> OnPostDeleteSectionAsync(int sectionId)
    {
        var section = await db.BuildingSections.FirstOrDefaultAsync(s => s.Id == sectionId);
        if (section is null)
            return NotFound();

        var buildingId = section.BuildingId;
        db.BuildingSections.Remove(section);   // patra zůstanou, jen se odpojí (SetNull)
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "section-deleted", "BuildingSection", sectionId.ToString(), section.Name);
        return RedirectToPage(new { open = $"building-{buildingId}" });
    }

    // ---------- Patra ----------

    public async Task<IActionResult> OnPostAddFloorAsync(int buildingId, string name, int? sectionId)
    {
        var maxOrder = await db.Floors.Where(f => f.BuildingId == buildingId)
            .MaxAsync(f => (int?)f.SortOrder) ?? 0;
        db.Floors.Add(new Floor
        {
            BuildingId = buildingId, Name = name.Trim(), SectionId = sectionId, SortOrder = maxOrder + 1,
        });
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "floor-created", "Floor", null, name);
        return RedirectToPage(new { open = $"building-{buildingId}" });
    }

    public async Task<IActionResult> OnPostDeleteFloorAsync(int floorId)
    {
        var floor = await db.Floors.FirstOrDefaultAsync(f => f.Id == floorId);
        if (floor is null)
            return NotFound();

        var hasReaders = await db.Readers.AnyAsync(r =>
            (r.Room != null && r.Room.FloorId == floorId)
            || (r.Corridor != null && r.Corridor.FloorId == floorId));
        if (hasReaders)
        {
            ErrorMessage = "Patro nelze smazat — obsahuje místnosti nebo chodby s přiřazenými čtečkami.";
            return RedirectToPage(new { open = $"building-{floor.BuildingId}" });
        }

        var buildingId = floor.BuildingId;
        db.Floors.Remove(floor);
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "floor-deleted", "Floor", floorId.ToString(), floor.Name);
        return RedirectToPage(new { open = $"building-{buildingId}" });
    }

    public async Task<IActionResult> OnPostUploadSchemaAsync(int floorId, IFormFile? file)
    {
        var floor = await db.Floors.FindAsync(floorId);
        if (floor is null)
            return NotFound();

        if (file is null || file.Length == 0)
        {
            ErrorMessage = "Vyberte soubor se schématem (PNG, JPEG nebo SVG).";
            return await RedirectToFloorAsync(floorId);
        }

        if (file.Length > 5 * 1024 * 1024)
        {
            ErrorMessage = "Schéma je příliš velké (max 5 MB).";
            return await RedirectToFloorAsync(floorId);
        }

        using var stream = new MemoryStream();
        await file.CopyToAsync(stream);
        floor.SchemaImage = stream.ToArray();
        floor.SchemaContentType = file.ContentType;
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "floor-schema-uploaded", "Floor", floorId.ToString(), file.FileName);
        Message = $"Schéma patra {floor.Name} nahráno.";
        return await RedirectToFloorAsync(floorId);
    }

    // ---------- Chodby ----------

    public async Task<IActionResult> OnPostAddCorridorAsync(int floorId, string name)
    {
        db.Corridors.Add(new Corridor { FloorId = floorId, Name = name.Trim() });
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "corridor-created", "Corridor", null, name);
        return await RedirectToFloorAsync(floorId);
    }

    public async Task<IActionResult> OnPostSetCorridorParentAsync(int floorId, int corridorId, int? parentCorridorId)
    {
        var corridor = await db.Corridors.FindAsync(corridorId);
        if (corridor is null)
            return NotFound();

        if (parentCorridorId is not null && await WouldCreateCorridorCycleAsync(corridorId, parentCorridorId.Value))
        {
            ErrorMessage = "Tuto nadřazenou chodbu nelze nastavit — vznikl by cyklus v řetězu chodeb.";
            return await RedirectToFloorAsync(floorId);
        }

        corridor.ParentCorridorId = parentCorridorId;
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "corridor-parent-set", "Corridor", corridorId.ToString(),
            parentCorridorId?.ToString() ?? "žádná");
        Message = $"Řetěz chodby {corridor.Name} uložen.";
        return await RedirectToFloorAsync(floorId);
    }

    public async Task<IActionResult> OnPostDeleteCorridorAsync(int floorId, int corridorId)
    {
        var corridor = await db.Corridors.Include(c => c.Readers).Include(c => c.Rooms)
            .FirstOrDefaultAsync(c => c.Id == corridorId);
        if (corridor is null)
            return NotFound();

        if (corridor.Readers.Count > 0 || corridor.Rooms.Count > 0)
        {
            ErrorMessage = "Chodbu nelze smazat — má přiřazené čtečky nebo místnosti.";
            return await RedirectToFloorAsync(floorId);
        }

        db.Corridors.Remove(corridor);
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "corridor-deleted", "Corridor", corridorId.ToString(), corridor.Name);
        return await RedirectToFloorAsync(floorId);
    }

    private async Task<bool> WouldCreateCorridorCycleAsync(int corridorId, int parentId)
    {
        if (corridorId == parentId)
            return true;

        var parents = await db.Corridors.Where(c => c.ParentCorridorId != null)
            .ToDictionaryAsync(c => c.Id, c => c.ParentCorridorId!.Value);

        int? current = parentId;
        var visited = new HashSet<int>();
        while (current is not null && visited.Add(current.Value))
        {
            if (current.Value == corridorId)
                return true;
            current = parents.TryGetValue(current.Value, out var p) ? p : null;
        }

        return false;
    }

    // ---------- Místnosti ----------

    public async Task<IActionResult> OnPostAddRoomAsync(int floorId, string name, int? corridorId)
    {
        db.Rooms.Add(new Room { FloorId = floorId, Name = name.Trim(), CorridorId = corridorId });
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "room-created", "Room", null, name);
        return await RedirectToFloorAsync(floorId);
    }

    public async Task<IActionResult> OnPostSetRoomCorridorAsync(int floorId, int roomId, int? corridorId)
    {
        var room = await db.Rooms.FindAsync(roomId);
        if (room is null)
            return NotFound();

        room.CorridorId = corridorId;
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "room-corridor-set", "Room", roomId.ToString(),
            corridorId?.ToString() ?? "žádná");
        Message = $"Místnost {room.Name} zařazena.";
        return await RedirectToFloorAsync(floorId);
    }

    public async Task<IActionResult> OnPostDeleteRoomAsync(int floorId, int roomId)
    {
        var room = await db.Rooms.Include(r => r.Readers).FirstOrDefaultAsync(r => r.Id == roomId);
        if (room is null)
            return NotFound();

        if (room.Readers.Count > 0)
        {
            ErrorMessage = "Místnost nelze smazat — má přiřazené čtečky.";
            return await RedirectToFloorAsync(floorId);
        }

        db.Rooms.Remove(room);
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "room-deleted", "Room", roomId.ToString(), room.Name);
        return await RedirectToFloorAsync(floorId);
    }
}
