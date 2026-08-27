using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog;

public class PlacesModel(AcsDbContext db, AuditService audit) : PageModel
{
    public List<Building> Buildings { get; private set; } = [];
    public List<Corridor> AllCorridors { get; private set; } = [];

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        Buildings = await db.Buildings
            .Include(b => b.Sections).ThenInclude(s => s.Floors)
            .Include(b => b.Floors).ThenInclude(f => f.Section)
            .Include(b => b.Floors).ThenInclude(f => f.Rooms).ThenInclude(r => r.Readers)
            .Include(b => b.Floors).ThenInclude(f => f.Corridors).ThenInclude(c => c.Readers)
            .Include(b => b.Floors).ThenInclude(f => f.Corridors).ThenInclude(c => c.Rooms)
            .Include(b => b.Floors).ThenInclude(f => f.Corridors).ThenInclude(c => c.ParentCorridor)
            .OrderBy(b => b.Name)
            .ToListAsync();

        AllCorridors = await db.Corridors
            .Include(c => c.Floor).ThenInclude(f => f!.Building)
            .OrderBy(c => c.Name)
            .ToListAsync();
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
        var building = await db.Buildings.Include(b => b.Floors).ThenInclude(f => f.Rooms)
            .FirstOrDefaultAsync(b => b.Id == buildingId);
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

    // ---------- Části budovy ----------

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
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteSectionAsync(int sectionId)
    {
        var section = await db.BuildingSections.Include(s => s.Floors)
            .FirstOrDefaultAsync(s => s.Id == sectionId);
        if (section is null)
            return NotFound();

        // Patra zůstávají, jen se odpojí od části (SetNull).
        db.BuildingSections.Remove(section);
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "section-deleted", "BuildingSection", sectionId.ToString(), section.Name);
        return RedirectToPage();
    }

    // ---------- Patra ----------

    public async Task<IActionResult> OnPostAddFloorAsync(int buildingId, string name, int? sectionId)
    {
        var maxOrder = await db.Floors.Where(f => f.BuildingId == buildingId)
            .MaxAsync(f => (int?)f.SortOrder) ?? 0;
        db.Floors.Add(new Floor
        {
            BuildingId = buildingId, Name = name.Trim(),
            SectionId = sectionId, SortOrder = maxOrder + 1,
        });
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "floor-created", "Floor", null, name);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteFloorAsync(int floorId)
    {
        var floor = await db.Floors
            .Include(f => f.Rooms).ThenInclude(r => r.Readers)
            .Include(f => f.Corridors).ThenInclude(c => c.Readers)
            .FirstOrDefaultAsync(f => f.Id == floorId);
        if (floor is null)
            return NotFound();

        if (floor.Rooms.Any(r => r.Readers.Count > 0) || floor.Corridors.Any(c => c.Readers.Count > 0))
        {
            ErrorMessage = "Patro nelze smazat — obsahuje místnosti nebo chodby s přiřazenými čtečkami.";
            return RedirectToPage();
        }

        db.Floors.Remove(floor);
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "floor-deleted", "Floor", floorId.ToString(), floor.Name);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUploadSchemaAsync(int floorId, IFormFile? file)
    {
        var floor = await db.Floors.FindAsync(floorId);
        if (floor is null)
            return NotFound();

        if (file is null || file.Length == 0)
        {
            ErrorMessage = "Vyberte soubor se schématem (PNG, JPEG nebo SVG).";
            return RedirectToPage();
        }

        if (file.Length > 5 * 1024 * 1024)
        {
            ErrorMessage = "Schéma je příliš velké (max 5 MB).";
            return RedirectToPage();
        }

        using var stream = new MemoryStream();
        await file.CopyToAsync(stream);
        floor.SchemaImage = stream.ToArray();
        floor.SchemaContentType = file.ContentType;
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "floor-schema-uploaded", "Floor", floorId.ToString(), file.FileName);
        Message = $"Schéma patra {floor.Name} nahráno.";
        return RedirectToPage();
    }

    // ---------- Chodby ----------

    public async Task<IActionResult> OnPostAddCorridorAsync(int floorId, string name)
    {
        db.Corridors.Add(new Corridor { FloorId = floorId, Name = name.Trim() });
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "corridor-created", "Corridor", null, name);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetCorridorParentAsync(int corridorId, int? parentCorridorId)
    {
        var corridor = await db.Corridors.FindAsync(corridorId);
        if (corridor is null)
            return NotFound();

        if (parentCorridorId is not null && await WouldCreateCorridorCycleAsync(corridorId, parentCorridorId.Value))
        {
            ErrorMessage = "Tuto nadřazenou chodbu nelze nastavit — vznikl by cyklus v řetězu chodeb.";
            return RedirectToPage();
        }

        corridor.ParentCorridorId = parentCorridorId;
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "corridor-parent-set", "Corridor", corridorId.ToString(),
            parentCorridorId?.ToString() ?? "žádná");
        Message = $"Řetěz chodby {corridor.Name} uložen.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteCorridorAsync(int corridorId)
    {
        var corridor = await db.Corridors
            .Include(c => c.Readers).Include(c => c.Rooms)
            .FirstOrDefaultAsync(c => c.Id == corridorId);
        if (corridor is null)
            return NotFound();

        if (corridor.Readers.Count > 0 || corridor.Rooms.Count > 0)
        {
            ErrorMessage = "Chodbu nelze smazat — má přiřazené čtečky nebo místnosti.";
            return RedirectToPage();
        }

        db.Corridors.Remove(corridor);
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "corridor-deleted", "Corridor", corridorId.ToString(), corridor.Name);
        return RedirectToPage();
    }

    /// <summary>Cyklus v řetězu chodeb: je „corridorId“ dosažitelný z „parentId“ po rodičích?</summary>
    private async Task<bool> WouldCreateCorridorCycleAsync(int corridorId, int parentId)
    {
        if (corridorId == parentId)
            return true;

        var parents = await db.Corridors
            .Where(c => c.ParentCorridorId != null)
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
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetRoomCorridorAsync(int roomId, int? corridorId)
    {
        var room = await db.Rooms.FindAsync(roomId);
        if (room is null)
            return NotFound();

        room.CorridorId = corridorId;
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "room-corridor-set", "Room", roomId.ToString(),
            corridorId?.ToString() ?? "žádná");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteRoomAsync(int roomId)
    {
        var room = await db.Rooms.Include(r => r.Readers).FirstOrDefaultAsync(r => r.Id == roomId);
        if (room is null)
            return NotFound();

        if (room.Readers.Count > 0)
        {
            ErrorMessage = "Místnost nelze smazat — má přiřazené čtečky.";
            return RedirectToPage();
        }

        db.Rooms.Remove(room);
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "room-deleted", "Room", roomId.ToString(), room.Name);
        return RedirectToPage();
    }
}
