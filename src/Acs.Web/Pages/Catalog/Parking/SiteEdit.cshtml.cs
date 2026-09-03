using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog.Parking;

public class SiteEditModel(AcsDbContext db, AuditService audit) : PageModel
{
    public Site Site { get; private set; } = null!;
    public List<ApprovalMatrix> Matrices { get; private set; } = [];
    public int PermitCount { get; private set; }

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var site = await db.Sites.FindAsync(id);
        if (site is null)
            return NotFound();

        Site = site;
        Matrices = await db.ApprovalMatrices.Where(m => m.IsActive).OrderBy(m => m.Name).ToListAsync();
        PermitCount = await db.ParkingPermitSites.CountAsync(s => s.SiteId == id);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(int id, string name, string? code, string? description,
        int sortOrder, int? approvalMatrixId, string? isActive)
    {
        var site = await db.Sites.FindAsync(id);
        if (site is null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorMessage = "Název je povinný.";
            return RedirectToPage(new { id });
        }

        site.Name = name.Trim();
        site.Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();
        site.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        site.SortOrder = sortOrder;
        site.ApprovalMatrixId = approvalMatrixId;
        site.IsActive = isActive == "true";
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "site-updated", "Site", id.ToString(), site.Name);
        Message = "Areál uložen.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var site = await db.Sites.FindAsync(id);
        if (site is null)
            return NotFound();

        if (await db.ParkingPermitSites.AnyAsync(s => s.SiteId == id))
        {
            ErrorMessage = "Areál se používá v povoleních — nelze smazat.";
            return RedirectToPage(new { id });
        }

        db.Sites.Remove(site);
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "site-deleted", "Site", id.ToString(), site.Name);
        return RedirectToPage("Sites");
    }
}
