using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Sync;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Acs.Web.Pages.Catalog.Employees;

public class IndexModel(AcsDbContext db, SyncJobRunner jobs) : PageModel
{
    public const string EmployeeJob = "Import zaměstnanců";
    public const string CardJob = "Synchronizace karet";

    public List<Employee> Employees { get; private set; } = [];
    public Dictionary<int, List<EmployeeIdentifier>> Identifiers { get; private set; } = new();
    public int TotalCount { get; private set; }
    public SyncJobStatus? EmployeeJobStatus => jobs.Get(EmployeeJob);
    public SyncJobStatus? CardJobStatus => jobs.Get(CardJob);

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        var query = db.Employees.AsQueryable();
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var term = Search.Trim();
            var normalized = EmployeeIdentifier.Normalize(term);
            query = query.Where(e =>
                e.FirstName.Contains(term)
                || e.LastName.Contains(term)
                || (e.Department != null && e.Department.Contains(term))
                || (e.PersonalNumber != null && e.PersonalNumber.Contains(term))
                || (e.CardNumber != null && e.CardNumber.Contains(term))
                || (e.AdAccount != null && e.AdAccount.Contains(term))
                // hledání i podle karty, SPZ nebo jiného identifikátoru
                || e.Identifiers.Any(i => i.Value.Contains(normalized)));
        }

        TotalCount = await query.CountAsync();
        Employees = await query
            .OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
            .Take(300)
            .ToListAsync();

        var ids = Employees.Select(e => e.Id).ToList();
        Identifiers = (await db.EmployeeIdentifiers
                .Where(i => ids.Contains(i.EmployeeId) && i.IsActive)
                .OrderBy(i => i.Type).ThenBy(i => i.Value)
                .ToListAsync())
            .GroupBy(i => i.EmployeeId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>Import z AD běží na pozadí — u velkých domén trvá minuty.</summary>
    public IActionResult OnPostSync()
    {
        var started = jobs.Start(EmployeeJob, async (services, ct) =>
        {
            var sync = services.GetRequiredService<EmployeeSyncService>();
            var result = await sync.SyncAsync(User.Identity?.Name, ct);
            var autoAssign = services.GetRequiredService<AutoAssignmentService>();
            var assigned = await autoAssign.RunAsync(User.Identity?.Name, ct);
            return $"{result}; {assigned}";
        });

        Message = started
            ? "Import zaměstnanců byl spuštěn na pozadí — průběh se zobrazuje na této stránce."
            : "Import zaměstnanců už běží.";
        return RedirectToPage();
    }

    public IActionResult OnPostSyncCards()
    {
        var started = jobs.Start(CardJob, async (services, ct) =>
        {
            var sync = services.GetRequiredService<CardSyncService>();
            return (await sync.SyncAsync(User.Identity?.Name, ct)).ToString();
        });

        Message = started
            ? "Synchronizace karet byla spuštěna na pozadí."
            : "Synchronizace karet už běží.";
        return RedirectToPage();
    }
}
