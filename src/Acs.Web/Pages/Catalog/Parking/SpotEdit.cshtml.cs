using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog.Parking;

public class SpotEditModel(AcsDbContext db, AuditService audit, ParkingAdminService parkingAdmin) : PageModel
{
    public ParkingSpot Spot { get; private set; } = null!;
    public List<Site> Sites { get; private set; } = [];

    /// <summary>Povolení, která se objeví na ceduli (vydaná, neodebraná, platná).</summary>
    public List<ParkingPermit> ActivePermits { get; private set; } = [];

    /// <summary>Kolik povolení (i historických) na místo odkazuje — kvůli mazání.</summary>
    public int PermitCount { get; private set; }

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var spot = await db.ParkingSpots.Include(s => s.Site).FirstOrDefaultAsync(s => s.Id == id);
        if (spot is null)
            return NotFound();

        Spot = spot;
        Sites = await db.Sites.OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync();
        ActivePermits = await parkingAdmin.GetSpotPermitsAsync(id);
        PermitCount = await db.ParkingPermits.CountAsync(p => p.ParkingSpotId == id);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(int id, int siteId, string code, string? location,
        string? note, int sortOrder, string? isActive)
    {
        var spot = await db.ParkingSpots.FindAsync(id);
        if (spot is null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(code))
        {
            ErrorMessage = "Označení místa je povinné.";
            return RedirectToPage(new { id });
        }

        var normalized = code.Trim().ToUpperInvariant();
        if (await db.ParkingSpots.AnyAsync(s => s.Id != id && s.SiteId == siteId && s.Code == normalized))
        {
            ErrorMessage = $"Místo „{normalized}“ v tomto areálu už existuje.";
            return RedirectToPage(new { id });
        }

        if (siteId != spot.SiteId && await db.ParkingPermits.AnyAsync(p => p.ParkingSpotId == id && p.RevokedAt == null && p.IssuedAt != null))
        {
            ErrorMessage = "Místo má přiřazená platná povolení — areál nelze změnit.";
            return RedirectToPage(new { id });
        }

        spot.SiteId = siteId;
        spot.Code = normalized;
        spot.Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
        spot.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        spot.SortOrder = sortOrder;
        spot.IsActive = isActive == "true";
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "parking-spot-updated", "ParkingSpot", id.ToString(), spot.Code);
        Message = "Místo uloženo.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUnassignAsync(int id, int permitId)
    {
        try
        {
            await parkingAdmin.AssignSpotAsync(permitId, null, User.Identity?.Name);
            Message = "Povolení z místa odebráno — ceduli vytiskněte znovu.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var spot = await db.ParkingSpots.FindAsync(id);
        if (spot is null)
            return NotFound();

        if (await db.ParkingPermits.AnyAsync(p => p.ParkingSpotId == id))
        {
            ErrorMessage = "Na místo se odkazují povolení — nelze smazat, jen deaktivovat.";
            return RedirectToPage(new { id });
        }

        db.ParkingSpots.Remove(spot);
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "parking-spot-deleted", "ParkingSpot", id.ToString(), spot.Code);
        return RedirectToPage("Spots");
    }
}
