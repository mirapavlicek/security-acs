using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Sync;
using Acs.Infrastructure.WinPak;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog.AccessLevels;

/// <summary>
/// Založení a úprava přístupové úrovně: název, popis, čtečky a u každé časová zóna.
/// Uložení zapíše celou definici do WIN-PAKu (čtečky mimo seznam z úrovně zmizí)
/// a obnoví zrcadlo.
/// </summary>
public class EditModel(AcsDbContext db, AccessLevelAdminService admin) : PageModel
{
    public sealed record ReaderRow(int Id, string Name, string? Building, string? ExternalId, bool HasLevel);

    public int? Id { get; private set; }
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string? Description { get; set; }

    /// <summary>Vybrané čtečky (id ACS) a paralelně jejich časové zóny (id WIN-PAK) — pole formuláře <c>readerIds</c>/<c>tz_{id}</c>.</summary>
    public Dictionary<int, string> Selected { get; private set; } = new();

    public List<ReaderRow> Readers { get; private set; } = [];
    public IReadOnlyList<WinPakTimeZone> TimeZones { get; private set; } = [];
    public string? TimeZonesError { get; private set; }
    public string? AccessTree { get; private set; }
    public bool TreeUnreadable { get; private set; }
    public bool IsActive { get; private set; } = true;

    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is { } levelId)
        {
            var level = await db.AccessLevels.Include(a => a.Entries).FirstOrDefaultAsync(a => a.Id == levelId);
            if (level is null)
                return NotFound();

            Id = level.Id;
            Name = level.Name;
            Description = level.Description;
            IsActive = level.IsActive;
            AccessTree = level.AccessTree;
            TreeUnreadable = AccessTreeParser.Parse(level.AccessTree) is null && !string.IsNullOrWhiteSpace(level.AccessTree);

            var byExternal = await db.Readers.Where(r => r.ExternalId != null)
                .ToDictionaryAsync(r => r.ExternalId!, r => r.Id);
            foreach (var entry in level.Entries)
            {
                if (entry.ReaderExternalId is { } ext && byExternal.TryGetValue(ext, out var readerId))
                    Selected.TryAdd(readerId, entry.TimeZoneExternalId ?? "");
            }
        }

        await LoadChoicesAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int? id, int[]? readerIds)
    {
        var assignments = (readerIds ?? [])
            .Select(readerId => new AccessLevelReaderAssignment(readerId, Request.Form[$"tz_{readerId}"].ToString()))
            .ToList();

        try
        {
            if (id is { } levelId)
            {
                await admin.UpdateAsync(User.Identity?.Name, levelId, Name, Description, assignments);
                return RedirectToPage("Index");
            }

            await admin.CreateAsync(User.Identity?.Name, Name, Description, assignments);
            return RedirectToPage("Index");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Id = id;
            Selected = assignments.ToDictionary(a => a.ReaderId, a => a.TimeZoneExternalId);
            await LoadChoicesAsync();
            return Page();
        }
    }

    private async Task LoadChoicesAsync()
    {
        Readers = await db.Readers
            .Where(r => r.IsActive && r.ExternalId != null)
            .OrderBy(r => r.Name)
            .Select(r => new ReaderRow(r.Id, r.Name,
                r.Room != null ? r.Room.Floor!.Building!.Name : r.Corridor != null ? r.Corridor.Floor!.Building!.Name : null,
                r.ExternalId, r.AccessLevelExternalId != null))
            .ToListAsync();

        try
        {
            TimeZones = await admin.GetTimeZonesAsync();
        }
        catch (Exception ex)
        {
            TimeZonesError = ex.Message;
        }
    }
}
