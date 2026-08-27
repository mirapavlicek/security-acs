using Acs.WinPakConnector.Models;
using Acs.WinPakConnector.Providers;
using Acs.WinPakConnector.Providers.Com;
using Microsoft.AspNetCore.Mvc;

namespace Acs.WinPakConnector.Pages.Features;

/// <summary>Systémové údaje, reporty, muster, filtry událostí a živý výpis událostí.</summary>
public class SystemModel(WinPakProviderCache providers) : FeaturePageModel(providers)
{
    public SystemInfoDto? SystemInfo { get; private set; }
    public IReadOnlyList<AccountDto> Accounts { get; private set; } = [];
    public IReadOnlyList<HardwareDeviceDto> Devices { get; private set; } = [];
    public IReadOnlyList<AccessAreaBranchDto> Branches { get; private set; } = [];
    public IReadOnlyList<string> EventFilters { get; private set; } = [];
    public IReadOnlyList<string> CommServerFilters { get; private set; } = [];
    public IReadOnlyList<WinPakEvent> Events { get; private set; } = [];
    public string? EventsNote { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(async () => Accounts = await Provider.GetAccountsAsync(ct));
        await LoadAsync(async () =>
        {
            var catalog = RequireCatalog();
            SystemInfo = await catalog.GetSystemInfoAsync(ct);
            Devices = await catalog.GetHardwareDevicesAsync(ct);
            Branches = await catalog.GetAccessAreaBranchesAsync(ct);
            EventFilters = await catalog.GetEventFiltersAsync(commServer: false, ct);
            CommServerFilters = await catalog.GetEventFiltersAsync(commServer: true, ct);
        });

        await LoadEventsAsync(ct);
    }

    private async Task LoadEventsAsync(CancellationToken ct)
    {
        if (Provider is not ComWinPakProvider com)
        {
            EventsNote = $"Režim {Mode} události z panelů neodebírá — dostupné jsou jen v režimu Com.";
            return;
        }

        try
        {
            Events = await com.GetRecentEventsAsync(50, ct);
        }
        catch (Exception ex)
        {
            EventsNote = ex.Message;
        }
    }

    public Task<IActionResult> OnPostLookupAsync(LookupKind kind, string? value, CancellationToken ct)
        => ActAsync($"Dotaz {kind}", async () =>
            (await RequireCatalog().LookupAsync(kind, value ?? "", ct)).Result ?? "bez výsledku");

    public Task<IActionResult> OnPostAssociatedTimeZoneAsync(
        string? accessLevelName, string? readerName, long? panelId, long? outputId, long? groupId,
        int? lockUnlock, CancellationToken ct)
        => ActAsync("Navázaná časová zóna", async () =>
        {
            var zone = await RequireCatalog().GetAssociatedTimeZoneAsync(
                new AssociatedTimeZoneQuery(accessLevelName, readerName, panelId, outputId, groupId, lockUnlock), ct);
            return zone is null ? "nenalezena" : $"{zone.Name} (id {zone.Id})";
        });

    public Task<IActionResult> OnPostAssociatedGroupAsync(string accessLevelName, string readerName, CancellationToken ct)
        => ActAsync("Navázaná skupina čtečky", async () =>
            await RequireCatalog().GetAssociatedGroupAsync(accessLevelName, readerName, ct) ?? "nenalezena");

    public Task<IActionResult> OnPostScheduleAsync(string scheduleId, CancellationToken ct)
        => ActAsync($"Plán reportu {scheduleId}", async () =>
        {
            var schedule = await RequireCatalog().GetScheduleAsync(scheduleId, ct);
            return schedule is null ? "nenalezen" : $"{schedule.Name}, typ {schedule.ScheduleType}, frekvence {schedule.Frequency}";
        });

    public Task<IActionResult> OnPostSaveScheduleAsync(
        string scheduleId, string name, int scheduleType, int frequency, int reportType,
        bool print, bool email, bool fax, CancellationToken ct)
        => ActAsync($"Uložení plánu reportu {scheduleId}",
            () => RequireCatalog().UpsertScheduleAsync(
                new ScheduleDto(scheduleId, name, null, scheduleType, frequency, reportType, print, email, fax), ct));

    public Task<IActionResult> OnPostDeleteScheduleAsync(string scheduleId, CancellationToken ct)
        => ActAsync($"Smazání plánu reportu {scheduleId}", () => RequireCatalog().DeleteScheduleAsync(scheduleId, ct));

    public Task<IActionResult> OnPostTemplateAsync(string templateId, CancellationToken ct)
        => ActAsync($"Šablona reportu {templateId}", async () =>
        {
            var template = await RequireCatalog().GetTemplateAsync(templateId, ct);
            return template is null ? "nenalezena" : $"{template.Name}, typ {template.Type}";
        });

    public Task<IActionResult> OnPostSaveTemplateAsync(
        string templateId, string name, int type, string? definition, CancellationToken ct)
        => ActAsync($"Uložení šablony reportu {templateId}",
            () => RequireCatalog().UpsertTemplateAsync(new TemplateDto(templateId, name, null, type, definition), ct));

    public Task<IActionResult> OnPostDeleteTemplateAsync(string templateId, CancellationToken ct)
        => ActAsync($"Smazání šablony reportu {templateId}", () => RequireCatalog().DeleteTemplateAsync(templateId, ct));

    public Task<IActionResult> OnPostBadgeAsync(string badgeId, CancellationToken ct)
        => ActAsync($"Odznak {badgeId}", async () =>
        {
            var badge = await RequireCatalog().GetBadgeAsync(badgeId, ct);
            return $"rozměr {badge.Height}×{badge.Width}, data {(badge.Data is null ? "chybí" : $"{badge.Data.Length} znaků")}";
        });

    public Task<IActionResult> OnPostMusterAsync(long areaId, long accountId, int sortField, int sortOrder, CancellationToken ct)
        => ActAsync("Muster report", async () =>
            (await RequireCatalog().GetMusterAsync(areaId, accountId, sortField, sortOrder, ct)).Raw ?? "bez dat");

    public Task<IActionResult> OnPostAddFilterAsync(long id, bool commServer, CancellationToken ct)
        => ActAsync($"Přidání filtru {id}", () => RequireCatalog().AddEventFilterAsync(id, commServer, ct));

    public Task<IActionResult> OnPostRemoveFilterAsync(long id, bool commServer, CancellationToken ct)
        => ActAsync($"Odebrání filtru {id}", () => RequireCatalog().RemoveEventFilterAsync(id, commServer, ct));
}
