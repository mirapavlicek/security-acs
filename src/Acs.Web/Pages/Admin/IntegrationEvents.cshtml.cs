using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Admin;

/// <summary>
/// Události z parkovacího systému a rozhodnutí ACS u brány — dohledání „proč mě závora
/// nepustila“ podle SPZ, areálu nebo zaměstnance.
/// </summary>
public class IntegrationEventsModel(AcsDbContext db) : PageModel
{
    public const int PageSize = 200;

    public List<IntegrationEvent> Events { get; private set; } = [];
    public List<Site> Sites { get; private set; } = [];

    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public int? SiteId { get; set; }
    [BindProperty(SupportsGet = true)] public bool DeniedOnly { get; set; }

    public int DeniedLast24h { get; private set; }
    public int TotalLast24h { get; private set; }
    public DateTime? LastReceivedAt { get; private set; }

    public async Task OnGetAsync()
    {
        Sites = await db.Sites.OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync();

        var since = DateTime.UtcNow.AddHours(-24);
        TotalLast24h = await db.IntegrationEvents.CountAsync(e => e.ReceivedAt >= since);
        DeniedLast24h = await db.IntegrationEvents.CountAsync(e => e.ReceivedAt >= since && e.Decision == "deny");
        LastReceivedAt = await db.IntegrationEvents.OrderByDescending(e => e.ReceivedAt)
            .Select(e => (DateTime?)e.ReceivedAt).FirstOrDefaultAsync();

        var query = db.IntegrationEvents
            .Include(e => e.Site)
            .Include(e => e.Employee)
            .AsQueryable();
        if (SiteId is { } siteId)
            query = query.Where(e => e.SiteId == siteId);
        if (DeniedOnly)
            query = query.Where(e => e.Decision == "deny");
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var needle = Search.Trim();
            var value = EmployeeIdentifier.Normalize(needle);
            query = query.Where(e => (e.CredentialValue != null && e.CredentialValue.Contains(value))
                                     || (e.AccessPointId != null && e.AccessPointId.Contains(needle))
                                     || (e.ExternalId != null && e.ExternalId.Contains(needle))
                                     || (e.TraceId != null && e.TraceId == needle)
                                     || (e.Employee != null && (e.Employee.LastName.Contains(needle) || e.Employee.FirstName.Contains(needle))));
        }

        Events = await query.OrderByDescending(e => e.OccurredAt).Take(PageSize).ToListAsync();
    }
}
