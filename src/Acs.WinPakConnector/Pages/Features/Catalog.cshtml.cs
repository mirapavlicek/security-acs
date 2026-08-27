using Acs.WinPakConnector.Models;
using Acs.WinPakConnector.Providers;
using Microsoft.AspNetCore.Mvc;

namespace Acs.WinPakConnector.Pages.Features;

/// <summary>Číselníky WIN-PAKu: časové zóny s intervaly a svátky se skupinami.</summary>
public class CatalogModel(WinPakProviderCache providers) : FeaturePageModel(providers)
{
    public IReadOnlyList<TimeZoneDto> TimeZones { get; private set; } = [];
    public IReadOnlyList<HolidayGroupDto> HolidayGroups { get; private set; } = [];

    [BindProperty(SupportsGet = true)] public string? TimeZoneId { get; set; }
    [BindProperty(SupportsGet = true)] public string? HolidayGroupId { get; set; }

    public IReadOnlyList<TimeZoneRangeDto> Ranges { get; private set; } = [];
    public TimeZoneUsageDto? Usage { get; private set; }
    public IReadOnlyList<TimeZoneDto> ReassignCandidates { get; private set; } = [];
    public IReadOnlyList<HolidayDto> HolidaysInGroup { get; private set; } = [];
    public IReadOnlyList<PanelDto> PanelsUsingGroup { get; private set; } = [];
    public string? DetailError { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(async () =>
        {
            var catalog = RequireCatalog();
            TimeZones = await catalog.GetTimeZonesAsync(ct);
            HolidayGroups = await catalog.GetHolidayGroupsAsync(ct);
        });

        await LoadDetailsAsync(ct);
    }

    private async Task LoadDetailsAsync(CancellationToken ct)
    {
        try
        {
            var catalog = RequireCatalog();

            if (!string.IsNullOrWhiteSpace(TimeZoneId))
            {
                Ranges = await catalog.GetTimeZoneRangesAsync(TimeZoneId, ct);
                Usage = await catalog.GetTimeZoneUsageAsync(TimeZoneId, ct);
                ReassignCandidates = await catalog.GetTimeZonesForReassignAsync(TimeZoneId, forOperators: false, ct);
            }

            if (!string.IsNullOrWhiteSpace(HolidayGroupId))
            {
                HolidaysInGroup = await catalog.GetHolidaysInGroupAsync(HolidayGroupId, ct);
                PanelsUsingGroup = await catalog.GetPanelsUsingHolidayGroupAsync(HolidayGroupId, ct);
            }
        }
        catch (Exception ex)
        {
            DetailError = ex.Message;
        }
    }

    // ---------- Časové zóny ----------

    public IActionResult OnPostOpenTimeZone(string timeZoneId) => RedirectToPage(new { timeZoneId });

    public Task<IActionResult> OnPostAddTimeZoneAsync(string name, string? description, bool simple, CancellationToken ct)
        => ActAsync($"Založení časové zóny {name}", async () =>
        {
            var catalog = RequireCatalog();
            if (simple)
            {
                await catalog.CreateTimeZoneAsync(new UpsertTimeZoneRequest(name, description), ct);
                return "hotovo";
            }

            return $"id {await catalog.AddTimeZoneAsync(new UpsertTimeZoneRequest(name, description), ct)}";
        });

    public Task<IActionResult> OnPostEditTimeZoneAsync(string currentName, string name, string? description, CancellationToken ct)
        => ActAsync($"Úprava časové zóny {currentName}",
            () => RequireCatalog().EditTimeZoneAsync(currentName, new UpsertTimeZoneRequest(name, description), ct));

    public Task<IActionResult> OnPostDeleteTimeZoneAsync(string timeZoneId, CancellationToken ct)
        => ActAsync($"Smazání časové zóny {timeZoneId}", () => RequireCatalog().DeleteTimeZoneAsync(timeZoneId, ct));

    public Task<IActionResult> OnPostSaveRangesAsync(
        string timeZoneId, int[] dayType, string[] startTime, string[] endTime, CancellationToken ct)
    {
        var ranges = dayType
            .Select((day, index) => new TimeZoneRangeRequest(day, Value(startTime, index), Value(endTime, index)))
            .Where(range => range.StartTime.Length > 0 && range.EndTime.Length > 0)
            .ToList();

        return ActAsync($"Uložení intervalů časové zóny {timeZoneId}",
            () => RequireCatalog().ConfigureTimeZoneRangesAsync(timeZoneId, ranges, ct));
    }

    private static string Value(string[] values, int index) => index < values.Length ? values[index] ?? "" : "";

    public Task<IActionResult> OnPostDeleteRangeAsync(string timeZoneId, string rangeId, CancellationToken ct)
        => ActAsync($"Smazání intervalu {rangeId}", () => RequireCatalog().DeleteTimeZoneRangeAsync(timeZoneId, rangeId, ct));

    public Task<IActionResult> OnPostReassignTimeZoneAsync(
        string timeZoneId, string newTimeZoneId, string? cardIds, string? accessLevelIds, CancellationToken ct)
        => ActAsync($"Přeřazení z časové zóny {timeZoneId}",
            () => RequireCatalog().ReassignTimeZoneAsync(new ReassignTimeZoneRequest(
                timeZoneId, newTimeZoneId,
                CardIds: Split(cardIds), AccessLevelIds: Split(accessLevelIds)), ct));

    public Task<IActionResult> OnPostRemoveFromPanelsAsync(string timeZoneId, string? panelIds, CancellationToken ct)
        => ActAsync($"Odebrání časové zóny {timeZoneId} z panelů",
            () => RequireCatalog().DeletePanelTimeZoneAsync(timeZoneId, Split(panelIds), ct));

    internal static IReadOnlyList<string> Split(string? values)
        => (values ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ---------- Svátky ----------

    public IActionResult OnPostOpenHolidayGroup(string holidayGroupId) => RedirectToPage(new { holidayGroupId });

    public Task<IActionResult> OnPostAddHolidayAsync(
        string name, int year, int month, int day, int type, bool allYears, CancellationToken ct)
        => ActAsync($"Založení svátku {name}",
            async () => $"id {await RequireCatalog().AddHolidayAsync(
                new UpsertHolidayRequest(name, year, month, day, type, allYears), ct)}");

    public Task<IActionResult> OnPostEditHolidayAsync(
        string currentName, string name, int year, int month, int day, int type, bool allYears, CancellationToken ct)
        => ActAsync($"Úprava svátku {currentName}",
            () => RequireCatalog().EditHolidayAsync(currentName,
                new UpsertHolidayRequest(name, year, month, day, type, allYears), ct));

    public Task<IActionResult> OnPostDeleteHolidayAsync(string holidayId, CancellationToken ct)
        => ActAsync($"Smazání svátku {holidayId}", () => RequireCatalog().DeleteHolidayAsync(holidayId, ct));

    public Task<IActionResult> OnPostAddHolidayGroupAsync(string name, string? holidayIds, CancellationToken ct)
        => ActAsync($"Založení skupiny svátků {name}",
            () => RequireCatalog().AddHolidayGroupAsync(new UpsertHolidayGroupRequest(name, Split(holidayIds)), ct));

    public Task<IActionResult> OnPostEditHolidayGroupAsync(string currentName, string name, string? holidayIds, CancellationToken ct)
        => ActAsync($"Úprava skupiny svátků {currentName}",
            () => RequireCatalog().EditHolidayGroupAsync(currentName,
                new UpsertHolidayGroupRequest(name, Split(holidayIds)), ct));

    public Task<IActionResult> OnPostDeleteHolidayGroupAsync(string holidayGroupId, CancellationToken ct)
        => ActAsync($"Smazání skupiny svátků {holidayGroupId}",
            () => RequireCatalog().DeleteHolidayGroupAsync(holidayGroupId, ct));
}
