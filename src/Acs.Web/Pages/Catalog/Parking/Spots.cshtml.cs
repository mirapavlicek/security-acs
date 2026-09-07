using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog.Parking;

/// <summary>Číselník parkovacích míst (stání s vyhrazeným parkováním) po areálech.</summary>
public class SpotsModel(AcsDbContext db, AuditService audit) : PageModel
{
    public List<ParkingSpot> Spots { get; private set; } = [];
    public List<Site> Sites { get; private set; } = [];

    /// <summary>Počet platných (vydaných, neodebraných, neexpirovaných) povolení na místě.</summary>
    public Dictionary<int, int> PermitCounts { get; private set; } = [];

    [BindProperty(SupportsGet = true)] public int? SiteId { get; set; }

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        Sites = await db.Sites.OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync();

        var query = db.ParkingSpots.Include(s => s.Site).AsQueryable();
        if (SiteId is { } siteId)
            query = query.Where(s => s.SiteId == siteId);
        Spots = await query
            .OrderBy(s => s.Site!.SortOrder).ThenBy(s => s.Site!.Name).ThenBy(s => s.SortOrder).ThenBy(s => s.Code)
            .ToListAsync();

        var today = DateTime.UtcNow.Date;
        PermitCounts = await db.ParkingPermits
            .Where(p => p.ParkingSpotId != null && p.IssuedAt != null && p.RevokedAt == null
                        && (p.ValidTo == null || p.ValidTo >= today))
            .GroupBy(p => p.ParkingSpotId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
    }

    public async Task<IActionResult> OnPostCreateAsync(int siteId, string code, string? location)
    {
        var site = await db.Sites.FindAsync(siteId);
        if (site is null || string.IsNullOrWhiteSpace(code))
        {
            ErrorMessage = "Vyberte areál a zadejte označení místa.";
            return RedirectToPage(new { SiteId });
        }

        var normalized = code.Trim().ToUpperInvariant();
        if (await db.ParkingSpots.AnyAsync(s => s.SiteId == siteId && s.Code == normalized))
        {
            ErrorMessage = $"Místo „{normalized}“ v areálu {site.Name} už existuje.";
            return RedirectToPage(new { SiteId });
        }

        var maxOrder = await db.ParkingSpots.Where(s => s.SiteId == siteId).MaxAsync(s => (int?)s.SortOrder) ?? 0;
        var spot = new ParkingSpot
        {
            SiteId = siteId,
            Code = normalized,
            Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim(),
            SortOrder = maxOrder + 1,
        };
        db.ParkingSpots.Add(spot);
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "parking-spot-created", "ParkingSpot", spot.Id.ToString(),
            $"{spot.Code} · {site.Name}");
        Message = $"Místo {spot.Code} ({site.Name}) založeno.";
        return RedirectToPage(new { SiteId });
    }
}
