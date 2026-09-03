using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Sync;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog.AccessLevels;

/// <summary>
/// Přístupové úrovně WIN-PAKu (skupiny oprávnění) zrcadlené v ACS: přehled,
/// synchronizace a vstup do správy. Úroveň s jedinou čtečkou je „úroveň té čtečky“
/// a ACS ji potřebuje mít namapovanou, aby čtečku mohla přidělovat — proto se tu
/// ukazuje i mapování.
/// </summary>
public class IndexModel(AcsDbContext db, AccessLevelSyncService sync, AccessLevelAdminService admin) : PageModel
{
    public List<AccessLevel> Levels { get; private set; } = [];

    /// <summary>Kolik čteček ACS má danou úroveň jako svou (Reader.AccessLevelExternalId).</summary>
    public Dictionary<string, int> MappedReaders { get; private set; } = new();

    public int ReadersWithoutLevel { get; private set; }

    [BindProperty(SupportsGet = true)] public bool ShowInactive { get; set; }

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        Levels = await db.AccessLevels.Include(a => a.Entries)
            .Where(a => ShowInactive || a.IsActive)
            .OrderBy(a => a.Name)
            .ToListAsync();
        MappedReaders = await db.Readers
            .Where(r => r.AccessLevelExternalId != null)
            .GroupBy(r => r.AccessLevelExternalId!)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
        ReadersWithoutLevel = await db.Readers.CountAsync(r => r.IsActive && r.ExternalId != null && r.AccessLevelExternalId == null);
    }

    public async Task<IActionResult> OnPostSyncAsync(bool refreshTrees)
    {
        try
        {
            var result = await sync.SyncAsync(User.Identity?.Name, refreshTrees);
            Message = $"Synchronizace úrovní: {result}.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Synchronizace se nezdařila: {ex.Message}";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        try
        {
            var name = await db.AccessLevels.Where(a => a.Id == id).Select(a => a.Name).FirstAsync();
            await admin.DeleteAsync(User.Identity?.Name, id);
            Message = $"Úroveň „{name}“ byla ve WIN-PAKu zrušena.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Zrušení se nezdařilo: {ex.Message}";
        }

        return RedirectToPage();
    }
}
