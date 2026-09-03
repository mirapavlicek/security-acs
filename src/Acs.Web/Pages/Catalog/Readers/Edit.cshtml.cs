using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Sync;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog.Readers;

public class EditModel(AcsDbContext db, AuditService audit, ReaderCleanupService cleanup) : PageModel
{
    [BindProperty]
    public Reader Reader { get; set; } = new() { Name = "" };

    public List<Room> Rooms { get; private set; } = [];
    public List<Corridor> Corridors { get; private set; } = [];
    public List<ApprovalMatrix> Matrices { get; private set; } = [];
    public List<ReaderDependency> Dependencies { get; private set; } = [];
    public List<Reader> DependencyCandidates { get; private set; } = [];

    [TempData] public string? ErrorMessage { get; set; }
    [TempData] public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        await LoadListsAsync(id);
        if (id is null)
            return Page();

        var reader = await db.Readers.FindAsync(id.Value);
        if (reader is null)
            return NotFound();

        Reader = reader;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Reader.Name))
        {
            ErrorMessage = "Název je povinný.";
            return RedirectToPage(new { id = Reader.Id == 0 ? (int?)null : Reader.Id });
        }

        // Čtečka je buď v místnosti, nebo na chodbě — při zadání obojího má přednost chodba.
        if (Reader.CorridorId is not null)
            Reader.RoomId = null;

        if (Reader.Id == 0)
        {
            Reader.Source = RecordSource.Manual;
            db.Readers.Add(Reader);
            await db.SaveChangesAsync();
            await audit.LogAsync(User.Identity?.Name, "reader-created", "Reader", Reader.Id.ToString(), Reader.Name);
        }
        else
        {
            var existing = await db.Readers.FindAsync(Reader.Id);
            if (existing is null)
                return NotFound();

            existing.Name = Reader.Name;
            existing.Description = Reader.Description;
            existing.PanelName = Reader.PanelName;
            existing.AccessLevelExternalId = Reader.AccessLevelExternalId;
            existing.RoomId = Reader.RoomId;
            existing.CorridorId = Reader.CorridorId;
            existing.ApprovalMatrixId = Reader.ApprovalMatrixId;
            existing.IsActive = Reader.IsActive;
            await db.SaveChangesAsync();
            await audit.LogAsync(User.Identity?.Name, "reader-updated", "Reader", existing.Id.ToString(), existing.Name);
        }

        return RedirectToPage("Index");
    }

    /// <summary>Odstraní čtečku: bez vazeb smaže, s vazbami deaktivuje a skryje.</summary>
    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        Message = (await cleanup.RemoveAsync([id], User.Identity?.Name)).ToString();
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostAddDependencyAsync(int id, int requiresReaderId)
    {
        if (id == requiresReaderId)
        {
            ErrorMessage = "Čtečka nemůže vyžadovat sama sebe.";
            return RedirectToPage(new { id });
        }

        if (await CreatesCycleAsync(id, requiresReaderId))
        {
            ErrorMessage = "Tuto závislost nelze přidat — vznikl by cyklus v řetězci čteček.";
            return RedirectToPage(new { id });
        }

        if (!await db.ReaderDependencies.AnyAsync(d => d.ReaderId == id && d.RequiresReaderId == requiresReaderId))
        {
            db.ReaderDependencies.Add(new ReaderDependency { ReaderId = id, RequiresReaderId = requiresReaderId });
            await db.SaveChangesAsync();
            await audit.LogAsync(User.Identity?.Name, "reader-dependency-added", "Reader", id.ToString(),
                $"vyžaduje {requiresReaderId}");
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveDependencyAsync(int id, int dependencyId)
    {
        var dependency = await db.ReaderDependencies.FindAsync(dependencyId);
        if (dependency is not null)
        {
            db.ReaderDependencies.Remove(dependency);
            await db.SaveChangesAsync();
            await audit.LogAsync(User.Identity?.Name, "reader-dependency-removed", "Reader", id.ToString());
        }

        return RedirectToPage(new { id });
    }

    /// <summary>Kontrola cyklu: je „id“ dosažitelné z „requiresReaderId“ po hranách závislostí?</summary>
    private async Task<bool> CreatesCycleAsync(int id, int requiresReaderId)
    {
        var edges = await db.ReaderDependencies
            .Select(d => new { d.ReaderId, d.RequiresReaderId })
            .ToListAsync();
        var graph = edges.GroupBy(e => e.ReaderId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.RequiresReaderId).ToList());

        var stack = new Stack<int>([requiresReaderId]);
        var visited = new HashSet<int>();
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == id)
                return true;
            if (!visited.Add(current) || !graph.TryGetValue(current, out var next))
                continue;
            foreach (var n in next)
                stack.Push(n);
        }

        return false;
    }

    private async Task LoadListsAsync(int? id)
    {
        Rooms = await db.Rooms
            .Include(r => r.Floor).ThenInclude(f => f!.Building)
            .OrderBy(r => r.Floor!.Building!.Name).ThenBy(r => r.Floor!.SortOrder).ThenBy(r => r.Name)
            .ToListAsync();

        Matrices = await db.ApprovalMatrices.Where(m => m.IsActive).OrderBy(m => m.Name).ToListAsync();

        Corridors = await db.Corridors
            .Include(c => c.Floor).ThenInclude(f => f!.Building)
            .OrderBy(c => c.Floor!.Building!.Name).ThenBy(c => c.Name)
            .ToListAsync();

        if (id is not null)
        {
            Dependencies = await db.ReaderDependencies
                .Include(d => d.RequiresReader)
                .Where(d => d.ReaderId == id)
                .ToListAsync();
            var usedIds = Dependencies.Select(d => d.RequiresReaderId).ToHashSet();
            usedIds.Add(id.Value);
            DependencyCandidates = await db.Readers
                .Where(r => !usedIds.Contains(r.Id) && r.IsActive)
                .OrderBy(r => r.Name)
                .ToListAsync();
        }
    }
}
