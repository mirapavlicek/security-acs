using Acs.WinPakConnector.Models;
using Acs.WinPakConnector.Providers;
using Microsoft.AspNetCore.Mvc;

namespace Acs.WinPakConnector.Pages.Features;

/// <summary>Správa přístupových úrovní — ACS je jen čte a přiřazuje kartám.</summary>
public class AccessLevelsModel(WinPakProviderCache providers) : FeaturePageModel(providers)
{
    public IReadOnlyList<AccessLevelDto> Levels { get; private set; } = [];
    public IReadOnlyList<ReaderDto> Readers { get; private set; } = [];
    public IReadOnlyList<TimeZoneDto> TimeZones { get; private set; } = [];

    [BindProperty(SupportsGet = true)] public string? Name { get; set; }

    public AccessLevelDto? Level { get; private set; }
    public string? AccessTree { get; private set; }
    public IReadOnlyList<CardDto> AffectedCards { get; private set; } = [];
    public IReadOnlyList<AccessLevelDto> ReassignCandidates { get; private set; } = [];
    public string? DetailError { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(async () => Levels = await Provider.GetAccessLevelsAsync(ct));
        await LoadAsync(async () => Readers = await Provider.GetReadersAsync(ct));
        await LoadAsync(async () => TimeZones = await RequireCatalog().GetTimeZonesAsync(ct));

        if (!string.IsNullOrWhiteSpace(Name))
            await LoadDetailAsync(Name, ct);
    }

    private async Task LoadDetailAsync(string name, CancellationToken ct)
    {
        try
        {
            var catalog = RequireCatalog();
            Level = await catalog.GetAccessLevelByNameAsync(name, ct);
            AccessTree = await catalog.GetAccessTreeAsync(name, ct);
            AffectedCards = await catalog.IsolateAccessLevelAsync(name, ct);
            ReassignCandidates = await catalog.GetAccessLevelsForReassignAsync(name, ct);
        }
        catch (Exception ex)
        {
            DetailError = ex.Message;
        }
    }

    public IActionResult OnPostOpen(string name) => RedirectToPage(new { name });

    public Task<IActionResult> OnPostCreateAsync(string name, string? description, CancellationToken ct)
        => ActAsync($"Založení úrovně {name}",
            () => RequireCatalog().CreateAccessLevelAsync(new CreateAccessLevelRequest(name, description), ct));

    /// <summary>Objektová varianta zápisu, kterou příručka uvádí vedle CreateAccessLevel.</summary>
    public Task<IActionResult> OnPostCreateObjectAsync(string name, string? description, CancellationToken ct)
        => ActAsync($"Založení úrovně {name} (objektové API)",
            () => RequireCatalog().AddAccessLevelAsync(new CreateAccessLevelRequest(name, description), ct));

    public Task<IActionResult> OnPostRenameAsync(string currentName, string name, string? description, CancellationToken ct)
        => ActAsync($"Úprava úrovně {currentName}",
            () => RequireCatalog().EditAccessLevelAsync(currentName, new CreateAccessLevelRequest(name, description), ct));

    public Task<IActionResult> OnPostConfigureReadersAsync(
        string name, string[] readerNames, string timeZoneName, CancellationToken ct)
        => ActAsync($"Nastavení čteček úrovně {name}",
            () => RequireCatalog().ConfigureAccessLevelAsync(
                name, new ConfigureAccessLevelRequest(readerNames, timeZoneName), ct));

    public Task<IActionResult> OnPostConfigureEntranceAsync(
        string name, string readerName, string timeZoneName, string? groupName, CancellationToken ct)
        => ActAsync($"Nastavení vstupu {readerName} v úrovni {name}",
            () => RequireCatalog().ConfigureEntranceAccessAsync(
                name, new ConfigureEntranceRequest(readerName, timeZoneName, groupName), ct));

    public Task<IActionResult> OnPostUpsertAsync(
        string? accessLevelId, string name, string? description,
        string[]? readerIds, string[]? readerTimeZoneIds, CancellationToken ct)
        => ActAsync($"Úplný zápis úrovně {name}",
            () => RequireCatalog().UpsertAccessLevelAsync(accessLevelId,
                new UpsertAccessLevelRequest(name, description, ReaderIds: readerIds, ReaderTimeZoneIds: readerTimeZoneIds), ct));

    public Task<IActionResult> OnPostReassignAsync(string name, string newAccessLevelName, CancellationToken ct)
        => ActAsync($"Přeřazení karet z úrovně {name}",
            () => RequireCatalog().ReassignAccessLevelAsync(name, new ReassignAccessLevelRequest(newAccessLevelName), ct));

    public Task<IActionResult> OnPostDeleteAsync(string name, CancellationToken ct)
        => ActAsync($"Smazání úrovně {name}", () => RequireCatalog().DeleteAccessLevelAsync(name, ct));

    public Task<IActionResult> OnPostDeleteWithReplacementAsync(
        string accessLevelId, string replacementId, bool multiple, CancellationToken ct)
        => ActAsync($"Smazání úrovně {accessLevelId} s náhradou {replacementId}",
            () => RequireCatalog().DeleteAccessLevelWithReplacementAsync(accessLevelId, replacementId, multiple, ct));
}
