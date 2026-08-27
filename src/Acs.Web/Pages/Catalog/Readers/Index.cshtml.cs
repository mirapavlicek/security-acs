using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Sync;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog.Readers;

public class IndexModel(AcsDbContext db, ReaderSyncService readerSync) : PageModel
{
    public List<Reader> Readers { get; private set; } = [];

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        var query = db.Readers
            .Include(r => r.Room).ThenInclude(room => room!.Floor).ThenInclude(f => f!.Building)
            .Include(r => r.Room).ThenInclude(room => room!.Floor).ThenInclude(f => f!.Section)
            .Include(r => r.Room).ThenInclude(room => room!.Corridor)
            .Include(r => r.Corridor).ThenInclude(c => c!.Floor).ThenInclude(f => f!.Building)
            .Include(r => r.Corridor).ThenInclude(c => c!.Floor).ThenInclude(f => f!.Section)
            .Include(r => r.Dependencies).ThenInclude(d => d.RequiresReader)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(Search))
        {
            query = query.Where(r =>
                r.Name.Contains(Search)
                || (r.PanelName != null && r.PanelName.Contains(Search))
                || (r.Description != null && r.Description.Contains(Search)));
        }

        Readers = await query.OrderBy(r => r.Name).ToListAsync();
    }

    public async Task<IActionResult> OnPostSyncAsync()
    {
        try
        {
            var result = await readerSync.SyncAsync(User.Identity?.Name);
            Message = $"Synchronizace dokončena: {result}.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Synchronizace selhala: {ex.Message}";
        }

        return RedirectToPage();
    }
}
