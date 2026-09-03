using Acs.WinPakConnector.Models;
using Acs.WinPakConnector.Providers;
using Microsoft.AspNetCore.Mvc;

namespace Acs.WinPakConnector.Pages.Features;

/// <summary>Panely, jejich výstupy a skupiny, přiřazení číselníků a hromadné povely.</summary>
public class PanelsModel(WinPakProviderCache providers) : FeaturePageModel(providers)
{
    public IReadOnlyList<PanelDto> Panels { get; private set; } = [];
    public IReadOnlyList<AccountDto> Accounts { get; private set; } = [];

    [BindProperty(SupportsGet = true)] public long? PanelId { get; set; }

    public IReadOnlyList<PanelPointDto> Outputs { get; private set; } = [];
    public IReadOnlyList<PanelPointDto> Groups { get; private set; } = [];
    public IReadOnlyList<TimeZoneDto> ConfiguredTimeZones { get; private set; } = [];
    public IReadOnlyList<TimeZoneDto> AvailableTimeZones { get; private set; } = [];
    public IReadOnlyList<HolidayGroupDto> ConfiguredHolidayGroups { get; private set; } = [];
    public IReadOnlyList<HolidayGroupDto> AllHolidayGroups { get; private set; } = [];
    public string? GroupCheck { get; private set; }
    public string? DetailError { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(async () => Panels = await RequireCatalog().GetPanelsAsync(ct));
        await LoadAsync(async () => Accounts = await Provider.GetAccountsAsync(ct));

        if (PanelId is { } panelId)
            await LoadDetailAsync(panelId, ct);
    }

    private async Task LoadDetailAsync(long panelId, CancellationToken ct)
    {
        IWinPakCatalogApi catalog;
        try
        {
            catalog = RequireCatalog();
        }
        catch (Exception ex)
        {
            DetailError = ex.Message;
            return;
        }

        // Každá část zvlášť: jedna odmítnutá (na ostrém třeba skupiny svátků)
        // nemá skrýt výstupy, skupiny a časové zóny, kvůli kterým se sem chodí.
        await LoadPartAsync("Výstupy", async () => Outputs = await catalog.GetPanelOutputsAsync(panelId, ct));
        await LoadPartAsync("Skupiny", async () => Groups = await catalog.GetPanelGroupsAsync(panelId, ct));
        await LoadPartAsync("Nastavené časové zóny", async () => ConfiguredTimeZones = await catalog.GetPanelTimeZonesAsync(panelId, configured: true, ct));
        await LoadPartAsync("Dostupné časové zóny", async () => AvailableTimeZones = await catalog.GetPanelTimeZonesAsync(panelId, configured: false, ct));
        await LoadPartAsync("Skupiny svátků panelu", async () => ConfiguredHolidayGroups = await catalog.GetPanelHolidayGroupsAsync(panelId, ct));
        await LoadPartAsync("Skupiny svátků", async () => AllHolidayGroups = await catalog.GetHolidayGroupsAsync(ct));
        await LoadPartAsync("Kontrola skupin", async ()
            => GroupCheck = (await catalog.LookupAsync(LookupKind.PanelGroupCheck, panelId.ToString(), ct)).Result);
    }

    private async Task LoadPartAsync(string name, Func<Task> load)
    {
        try
        {
            await load();
        }
        catch (Exception ex)
        {
            DetailError = DetailError is null ? $"{name}: {ex.Message}" : $"{DetailError}\n{name}: {ex.Message}";
        }
    }

    public Task<IActionResult> OnPostTimeZonesAsync(long panelId, string[] timeZoneIds, CancellationToken ct)
        => ActAsync($"Nastavení časových zón panelu {panelId}",
            () => RequireCatalog().ConfigurePanelTimeZonesAsync(panelId, timeZoneIds, ct));

    public Task<IActionResult> OnPostHolidayGroupsAsync(long panelId, string[] holidayGroupIds, CancellationToken ct)
        => ActAsync($"Nastavení skupin svátků panelu {panelId}",
            () => RequireCatalog().ConfigurePanelHolidayGroupsAsync(panelId, holidayGroupIds, ct));

    public Task<IActionResult> OnPostOutputTimeZoneAsync(
        long panelId, long outputId, string timeZoneId, int? lockUnlock, CancellationToken ct)
        => ActAsync($"Nastavení časové zóny výstupu {outputId}",
            () => RequireCatalog().ConfigureOutputTimeZoneAsync(panelId, outputId, timeZoneId, lockUnlock, ct));

    public Task<IActionResult> OnPostGroupTimeZoneAsync(long panelId, long groupId, string timeZoneId, CancellationToken ct)
        => ActAsync($"Nastavení časové zóny skupiny {groupId}",
            () => RequireCatalog().ConfigureGroupTimeZoneAsync(panelId, groupId, timeZoneId, ct));

    public Task<IActionResult> OnPostInitializeAsync(long hid, int panelType, string? tasks, CancellationToken ct)
        => ActAsync($"Inicializace panelu {hid}", () => RequireCatalog().InitializePanelAsync(
            hid, new PanelInitializeRequest(panelType, ParseTasks(tasks)), ct));

    /// <summary>Kroky inicializace se zadávají jako čísla oddělená čárkou (viz číselník panelInitTask).</summary>
    internal static IReadOnlyList<int> ParseTasks(string? tasks)
        => (tasks ?? "")
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var task) ? task : -1)
            .Where(task => task >= 0)
            .ToList();

    public Task<IActionResult> OnPostCancelInitializeAsync(long hid, CancellationToken ct)
        => ActAsync($"Zrušení inicializace panelu {hid}", () => RequireCatalog().CancelPanelInitializeAsync(hid, ct));

    public Task<IActionResult> OnPostRefreshTimeZonesAsync(long hid, CancellationToken ct)
        => ActAsync($"Nahrání časových zón do panelu {hid}", () => RequireCatalog().RefreshPanelTimeZonesAsync(hid, ct));

    public Task<IActionResult> OnPostLockAllAsync(long accountId, bool shouldLock, CancellationToken ct)
        => ActAsync($"{(shouldLock ? "Zamknutí" : "Odemknutí")} všech dveří účtu {accountId}",
            () => RequireCatalog().LockUnlockAllDoorsAsync(accountId, shouldLock, ct));

    public Task<IActionResult> OnPostRefreshDoorsAsync(long accountId, CancellationToken ct)
        => ActAsync($"Obnovení stavu dveří účtu {accountId}",
            async () => $"status {await RequireCatalog().RefreshDoorsAsync(accountId, ct)}");

    public Task<IActionResult> OnPostDoorScheduleAsync(
        long panelHid, int panelType, long entranceId, long entrancePointId, long timeZoneId, CancellationToken ct)
        => ActAsync($"Spuštění door schedule na panelu {panelHid}",
            () => RequireCatalog().ExecuteDoorScheduleAsync(
                new DoorScheduleRequest(panelHid, panelType, entranceId, entrancePointId, timeZoneId), ct));
}
