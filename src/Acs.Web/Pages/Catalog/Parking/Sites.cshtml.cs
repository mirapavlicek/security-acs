using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog.Parking;

public class SitesModel(AcsDbContext db, AuditService audit) : PageModel
{
    public List<Site> Sites { get; private set; } = [];

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        Sites = await db.Sites.Include(s => s.ApprovalMatrix)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync(string name, string? code)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorMessage = "Zadejte název areálu.";
            return RedirectToPage();
        }

        var maxOrder = await db.Sites.MaxAsync(s => (int?)s.SortOrder) ?? 0;
        var site = new Site
        {
            Name = name.Trim(),
            Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant(),
            SortOrder = maxOrder + 1,
        };
        db.Sites.Add(site);
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "site-created", "Site", site.Id.ToString(), site.Name);
        return RedirectToPage("SiteEdit", new { id = site.Id });
    }
}
