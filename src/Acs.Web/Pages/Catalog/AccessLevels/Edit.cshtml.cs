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
    public sealed record ReaderRow(int Id, string Name, string? Building, string? ExternalId, bool HasLevel, string? Location);

    /// <summary>Čtečka ve stromu WIN-PAKu doplněná o to, co o ní ví ACS.</summary>
    public sealed record TreeReaderRow(string Name, string? TimeZoneName, int? ReaderId, string? ReaderName, string? Location);

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

    /// <summary>Strom přístupů k zobrazení (větve → čtečky); null = strom není nebo mu ACS nerozumí.</summary>
    public IReadOnlyList<AccessTreeBranch>? Outline { get; private set; }
    public Dictionary<string, TreeReaderRow> TreeReaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Čtečky ve stromu, které ACS nezná (nejsou v číselníku pod tím číslem).</summary>
    public List<string> UnknownReaders { get; private set; } = [];

    /// <summary>Čtečky v úrovni, které ACS zná jen z dokumentace (bez id WIN-PAKu) — zápis by je z úrovně vyřadil.</summary>
    public List<string> ReadersWithoutId { get; private set; } = [];

    public bool CanSave => TimeZonesError is null && ReadersWithoutId.Count == 0;

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
            Outline = AccessTreeOutline.Parse(level.AccessTree);

            await LoadChoicesAsync(level);
            return Page();
        }

        await LoadChoicesAsync(null);
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
            await LoadChoicesAsync(null);
            Selected = assignments.ToDictionary(a => a.ReaderId, a => a.TimeZoneExternalId);
            return Page();
        }
    }

    private async Task LoadChoicesAsync(AccessLevel? level)
    {
        try
        {
            TimeZones = await admin.GetTimeZonesAsync();
        }
        catch (Exception ex)
        {
            TimeZonesError = ex.Message;
        }

        var all = await db.Readers
            .Include(r => r.Room!).ThenInclude(r => r.Floor!).ThenInclude(f => f.Building)
            .Include(r => r.Corridor!).ThenInclude(c => c.Floor!).ThenInclude(f => f.Building)
            .Where(r => r.IsActive)
            .OrderBy(r => r.Name)
            .ToListAsync();

        var inLevel = new HashSet<int>();
        if (level is not null)
        {
            // Položky jsou spárované už ze synchronizace; tady se jen dohledá, co přibylo od ní.
            var matcher = new ReaderMatcher(all);
            var zonesByName = TimeZones.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
            var byId = all.ToDictionary(r => r.Id);

            foreach (var entry in level.Entries)
            {
                var reader = entry.ReaderId is { } rid && byId.TryGetValue(rid, out var known) ? known : matcher.Match(entry);
                var zone = entry.TimeZoneExternalId
                           ?? (entry.TimeZoneName is not null ? zonesByName.GetValueOrDefault(entry.TimeZoneName) : null)
                           ?? "";
                var key = entry.ReaderName ?? entry.ReaderExternalId ?? "?";
                var location = reader is { Room: null, Corridor: null } or null ? null : reader.LocationPath();
                TreeReaders.TryAdd(key, new TreeReaderRow(key, entry.TimeZoneName, reader?.Id, reader?.Name, location));

                if (reader is null)
                {
                    if (!UnknownReaders.Contains(key))
                        UnknownReaders.Add(key);
                    continue;
                }

                inLevel.Add(reader.Id);
                Selected.TryAdd(reader.Id, zone);
                if (string.IsNullOrWhiteSpace(reader.ExternalId) && !ReadersWithoutId.Contains(reader.Name))
                    ReadersWithoutId.Add(reader.Name);
            }
        }

        Readers = all
            .Where(r => r.ExternalId != null || inLevel.Contains(r.Id))
            .Select(r => new ReaderRow(r.Id, r.Name,
                r.Room?.Floor?.Building?.Name ?? r.Corridor?.Floor?.Building?.Name,
                r.ExternalId, r.AccessLevelExternalId != null,
                r.Room is null && r.Corridor is null ? null : r.LocationPath()))
            .ToList();
    }
}
