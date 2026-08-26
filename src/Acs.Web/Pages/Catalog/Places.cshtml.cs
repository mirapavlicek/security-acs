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

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
        => Buildings = await db.Buildings
            .Include(b => b.Floors).ThenInclude(f => f.Rooms).ThenInclude(r => r.Readers)
            .OrderBy(b => b.Name)
            .ToListAsync();

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

        if (building.Floors.SelectMany(f => f.Rooms).Any(r => r.Readers.Count > 0)
            || await db.Readers.AnyAsync(r => r.Room != null && r.Room.Floor!.BuildingId == buildingId))
        {
            ErrorMessage = "Budovu nelze smazat — obsahuje místnosti s přiřazenými čtečkami.";
            return RedirectToPage();
        }

        db.Buildings.Remove(building);
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "building-deleted", "Building", buildingId.ToString(), building.Name);
        Message = $"Budova {building.Name} smazána.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddFloorAsync(int buildingId, string name)
    {
        var maxOrder = await db.Floors.Where(f => f.BuildingId == buildingId)
            .MaxAsync(f => (int?)f.SortOrder) ?? 0;
        db.Floors.Add(new Floor { BuildingId = buildingId, Name = name.Trim(), SortOrder = maxOrder + 1 });
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "floor-created", "Floor", null, name);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteFloorAsync(int floorId)
    {
        var floor = await db.Floors.Include(f => f.Rooms).ThenInclude(r => r.Readers)
            .FirstOrDefaultAsync(f => f.Id == floorId);
        if (floor is null)
            return NotFound();

        if (floor.Rooms.Any(r => r.Readers.Count > 0))
        {
            ErrorMessage = "Patro nelze smazat — obsahuje místnosti s přiřazenými čtečkami.";
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

    public async Task<IActionResult> OnPostAddRoomAsync(int floorId, string name)
    {
        db.Rooms.Add(new Room { FloorId = floorId, Name = name.Trim() });
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "room-created", "Room", null, name);
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
