using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Sync;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog.Readers;

public class IndexModel(
    AcsDbContext db,
    ReaderSyncService readerSync,
    ReaderGroupService groupService,
    ReaderCleanupService cleanup,
    AuditService audit) : PageModel
{
    /// <summary>Hodnoty filtru aktivity.</summary>
    public const string ActiveOnlyFilter = "active";
    public const string InactiveOnlyFilter = "inactive";

    /// <summary>Hodnota filtru matice, která znamená „čtečky bez matice“.</summary>
    public const string NoMatrixFilter = "none";

    public List<Reader> Readers { get; private set; } = [];
    public List<ApprovalMatrix> Matrices { get; private set; } = [];
    public List<Building> Buildings { get; private set; } = [];
    public List<Floor> Floors { get; private set; } = [];
    public List<ReaderGroup> Groups { get; private set; } = [];

    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public int? BuildingId { get; set; }
    [BindProperty(SupportsGet = true)] public int? FloorId { get; set; }
    [BindProperty(SupportsGet = true)] public int? GroupId { get; set; }

    /// <summary>Prázdné = všechny, <see cref="NoMatrixFilter"/> = bez matice, jinak id matice.</summary>
    [BindProperty(SupportsGet = true)] public string? Matrix { get; set; }

    /// <summary>Prázdné = všechny, <see cref="ActiveOnlyFilter"/> nebo <see cref="InactiveOnlyFilter"/>.</summary>
    [BindProperty(SupportsGet = true)] public string? Active { get; set; }

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public bool HasFilter =>
        !string.IsNullOrWhiteSpace(Search) || BuildingId is not null || FloorId is not null
        || GroupId is not null || !string.IsNullOrWhiteSpace(Matrix) || !string.IsNullOrWhiteSpace(Active);

    public int InactiveCount { get; private set; }

    public async Task OnGetAsync()
    {
        var query = (await FilteredQueryAsync())
            .Include(r => r.Room).ThenInclude(room => room!.Floor).ThenInclude(f => f!.Building)
            .Include(r => r.Room).ThenInclude(room => room!.Floor).ThenInclude(f => f!.Section)
            .Include(r => r.Room).ThenInclude(room => room!.Corridor)
            .Include(r => r.Corridor).ThenInclude(c => c!.Floor).ThenInclude(f => f!.Building)
            .Include(r => r.Corridor).ThenInclude(c => c!.Floor).ThenInclude(f => f!.Section)
            .Include(r => r.ApprovalMatrix)
            .Include(r => r.Dependencies).ThenInclude(d => d.RequiresReader);

        Readers = await query.OrderBy(r => r.Name).ToListAsync();
        InactiveCount = Readers.Count(r => !r.IsActive);
        Matrices = await db.ApprovalMatrices.Where(m => m.IsActive).OrderBy(m => m.Name).ToListAsync();
        Buildings = await db.Buildings.OrderBy(b => b.Name).ToListAsync();
        Floors = await db.Floors.Include(f => f.Building)
            .Where(f => BuildingId == null || f.BuildingId == BuildingId)
            .OrderBy(f => f.Building!.Name).ThenBy(f => f.SortOrder)
            .ToListAsync();
        Groups = await db.ReaderGroups.OrderBy(g => g.Name).ToListAsync();
    }

    /// <summary>Dotaz zúžený aktuálním filtrem — sdílí ho výpis i hromadné přiřazení „všem odpovídajícím“.</summary>
    private async Task<IQueryable<Reader>> FilteredQueryAsync(CancellationToken ct = default)
    {
        var query = db.Readers.AsQueryable();

        if (!string.IsNullOrWhiteSpace(Search))
        {
            query = query.Where(r =>
                r.Name.Contains(Search)
                || (r.PanelName != null && r.PanelName.Contains(Search))
                || (r.Description != null && r.Description.Contains(Search)));
        }

        if (BuildingId is not null)
        {
            query = query.Where(r =>
                (r.Room != null && r.Room.Floor!.BuildingId == BuildingId)
                || (r.Corridor != null && r.Corridor.Floor!.BuildingId == BuildingId));
        }

        if (FloorId is not null)
        {
            query = query.Where(r =>
                (r.Room != null && r.Room.FloorId == FloorId)
                || (r.Corridor != null && r.Corridor.FloorId == FloorId));
        }

        if (GroupId is not null)
        {
            // Skupina se rozbaluje rekurzivně, aby filtr sedl i na vnořené skupiny.
            var memberIds = await groupService.ExpandReaderIdsAsync(GroupId.Value, ct);
            query = query.Where(r => memberIds.Contains(r.Id));
        }

        if (Matrix == NoMatrixFilter)
            query = query.Where(r => r.ApprovalMatrixId == null);
        else if (int.TryParse(Matrix, out var matrixId))
            query = query.Where(r => r.ApprovalMatrixId == matrixId);

        if (Active == ActiveOnlyFilter)
            query = query.Where(r => r.IsActive);
        else if (Active == InactiveOnlyFilter)
            query = query.Where(r => !r.IsActive);

        return query;
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

        return RedirectToFilteredPage();
    }

    /// <summary>Přiřadí matici zaškrtnutým čtečkám.</summary>
    public async Task<IActionResult> OnPostAssignAsync(int[] readerIds, int? matrixId)
    {
        if (readerIds.Length == 0)
        {
            ErrorMessage = "Nevybrali jste žádnou čtečku.";
            return RedirectToFilteredPage();
        }

        var matrix = await ResolveMatrixAsync(matrixId);
        if (matrixId is not null && matrix is null)
            return RedirectToFilteredPage();

        var changed = await db.Readers.Where(r => readerIds.Contains(r.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ApprovalMatrixId, matrixId));

        await LogBulkAsync(changed, matrix, "výběr");
        return RedirectToFilteredPage();
    }

    /// <summary>Přiřadí matici všem čtečkám, které odpovídají aktuálnímu filtru (i mimo obrazovku).</summary>
    public async Task<IActionResult> OnPostAssignFilteredAsync(int? matrixId)
    {
        var matrix = await ResolveMatrixAsync(matrixId);
        if (matrixId is not null && matrix is null)
            return RedirectToFilteredPage();

        var query = await FilteredQueryAsync();
        var changed = await query.ExecuteUpdateAsync(s => s.SetProperty(r => r.ApprovalMatrixId, matrixId));

        await LogBulkAsync(changed, matrix, HasFilter ? "filtr" : "všechny čtečky");
        return RedirectToFilteredPage();
    }

    /// <summary>Smaže označené čtečky — jen neaktivní bez vazeb, ostatní přeskočí s důvodem.</summary>
    public async Task<IActionResult> OnPostDeleteAsync(int[] readerIds)
    {
        if (readerIds.Length == 0)
        {
            ErrorMessage = "Nevybrali jste žádnou čtečku.";
            return RedirectToFilteredPage();
        }

        Message = (await cleanup.DeleteAsync(readerIds, User.Identity?.Name)).ToString();
        return RedirectToFilteredPage();
    }

    /// <summary>Smaže všechny neaktivní čtečky bez vazeb, které odpovídají aktuálnímu filtru.</summary>
    public async Task<IActionResult> OnPostDeleteFilteredAsync()
    {
        var query = await FilteredQueryAsync();
        var ids = await query.Where(r => !r.IsActive).Select(r => r.Id).ToListAsync();
        if (ids.Count == 0)
        {
            ErrorMessage = "Podle aktuálního filtru není žádná neaktivní čtečka.";
            return RedirectToFilteredPage();
        }

        Message = (await cleanup.DeleteAsync(ids, User.Identity?.Name)).ToString();
        return RedirectToFilteredPage();
    }

    private async Task<ApprovalMatrix?> ResolveMatrixAsync(int? matrixId)
    {
        if (matrixId is null)
            return null;

        var matrix = await db.ApprovalMatrices.FirstOrDefaultAsync(m => m.Id == matrixId);
        if (matrix is null)
            ErrorMessage = "Vybraná matice neexistuje.";
        else if (!matrix.IsActive)
            ErrorMessage = $"Matice {matrix.Name} je neaktivní — žádosti by se podle ní neschvalovaly.";

        return ErrorMessage is null ? matrix : null;
    }

    private async Task LogBulkAsync(int changed, ApprovalMatrix? matrix, string scope)
    {
        var target = matrix?.Name ?? "bez schvalování";
        await audit.LogAsync(User.Identity?.Name, "readers-matrix-bulk-assigned", "Reader", null,
            $"{changed} čteček ({scope}) → {target}");
        Message = $"Matice „{target}“ nastavena u {changed} čteček.";
    }

    private RedirectToPageResult RedirectToFilteredPage() => RedirectToPage(new
    {
        search = Search,
        buildingId = BuildingId,
        floorId = FloorId,
        groupId = GroupId,
        matrix = Matrix,
        active = Active,
    });
}
