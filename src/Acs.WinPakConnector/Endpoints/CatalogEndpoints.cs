using Acs.WinPakConnector.Models;
using Acs.WinPakConnector.Providers;

namespace Acs.WinPakConnector.Endpoints;

/// <summary>
/// REST nad rozšířenou částí WIN-PAK API (<see cref="IWinPakCatalogApi"/>).
/// Providery, které ji neumí, tady končí na 501 — díky <see cref="Catalog"/>.
/// </summary>
public static class CatalogEndpoints
{
    private static IWinPakCatalogApi Catalog(IWinPakProvider provider)
        => provider as IWinPakCatalogApi
           ?? throw new NotSupportedException(
               $"Režim {provider.Mode} tuto část WIN-PAK API nepodporuje. Použijte režim Com.");

    public static void MapCatalog(this IEndpointRouteBuilder api)
    {
        MapAccessLevels(api);
        MapCards(api);
        MapCardHolders(api);
        MapTimeZones(api);
        MapHolidays(api);
        MapHardware(api);
        MapSystem(api);
        MapCommands(api);
    }

    private static void MapAccessLevels(IEndpointRouteBuilder api)
    {
        api.MapGet("/access-levels/{name}", async (string name, IWinPakProvider p, CancellationToken ct)
            => await Catalog(p).GetAccessLevelByNameAsync(name, ct) is { } level
                ? Results.Ok(level)
                : Results.NotFound());

        api.MapGet("/access-levels/{name}/tree", async (string name, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(new { accessTree = await Catalog(p).GetAccessTreeAsync(name, ct) }));

        api.MapGet("/access-levels/{name}/cards", async (string name, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).IsolateAccessLevelAsync(name, ct)));

        api.MapGet("/access-levels/{name}/reassign-candidates", async (string name, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetAccessLevelsForReassignAsync(name, ct)));

        api.MapPost("/access-levels", async (CreateAccessLevelRequest request, IWinPakProvider p, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "Name je povinné." });

            await Catalog(p).CreateAccessLevelAsync(request, ct);
            return Results.Created($"/api/v1/access-levels/{Uri.EscapeDataString(request.Name)}", null);
        });

        api.MapPut("/access-levels/{id}", async (string id, UpsertAccessLevelRequest request, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).UpsertAccessLevelAsync(id, request, ct);
            return Results.NoContent();
        });

        api.MapPost("/access-levels/{name}/readers", async (string name, ConfigureAccessLevelRequest request, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).ConfigureAccessLevelAsync(name, request, ct);
            return Results.NoContent();
        });

        api.MapPost("/access-levels/{name}/entrance", async (string name, ConfigureEntranceRequest request, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).ConfigureEntranceAccessAsync(name, request, ct);
            return Results.NoContent();
        });

        api.MapPost("/access-levels/{name}/reassign", async (string name, ReassignAccessLevelRequest request, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).ReassignAccessLevelAsync(name, request, ct);
            return Results.NoContent();
        });

        api.MapDelete("/access-levels/{name}", async (string name, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).DeleteAccessLevelAsync(name, ct);
            return Results.NoContent();
        });
    }

    private static void MapCards(IEndpointRouteBuilder api)
    {
        api.MapGet("/cards", async (bool? withoutHolder, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetCardsAsync(withoutHolder ?? false, ct)));

        api.MapPost("/cards/bulk", async (BulkAddCardsRequest request, IWinPakProvider p, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.StartNumber) || string.IsNullOrWhiteSpace(request.StopNumber))
                return Results.BadRequest(new { error = "StartNumber a StopNumber jsou povinné." });

            await Catalog(p).BulkAddCardsAsync(request, ct);
            return Results.NoContent();
        });

        api.MapPost("/cards/bulk-delete", async (BulkDeleteCardsRequest request, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).BulkDeleteCardsAsync(request, ct);
            return Results.NoContent();
        });
    }

    private static void MapCardHolders(IEndpointRouteBuilder api)
    {
        api.MapDelete("/cardholders/{id}", async (string id, bool? keepCards, bool? keepImages, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).DeleteCardHolderAsync(id,
                new DeleteCardHolderOptions(!(keepCards ?? false), !(keepImages ?? false)), ct);
            return Results.NoContent();
        });

        api.MapGet("/cardholders/search-fields", async (IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetCardHolderSearchFieldsAsync(ct)));

        api.MapPost("/cardholders/search", async (CardHolderSearchRequest request, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).SearchCardHoldersAsync(request, ct)));

        api.MapGet("/note-field-templates", async (IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetNoteFieldTemplatesAsync(ct)));

        api.MapGet("/cardholders/{id}/photo/{index:int}", async (string id, int index, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetCardHolderImageAsync(id, index, signature: false, ct)));

        api.MapGet("/cardholders/{id}/signature/{index:int}", async (string id, int index, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetCardHolderImageAsync(id, index, signature: true, ct)));

        api.MapPut("/cardholders/{id}/photo/{index:int}", async (string id, int index, ImportImageRequest request, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).ImportCardHolderImageAsync(id, index, signature: false, request.ContentBase64, ct);
            return Results.NoContent();
        });

        api.MapPut("/cardholders/{id}/signature/{index:int}", async (string id, int index, ImportImageRequest request, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).ImportCardHolderImageAsync(id, index, signature: true, request.ContentBase64, ct);
            return Results.NoContent();
        });

        api.MapDelete("/cardholders/{id}/photo/{index:int}", async (string id, int index, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).DeleteCardHolderImageAsync(id, index, signature: false, ct);
            return Results.NoContent();
        });

        api.MapDelete("/cardholders/{id}/signature/{index:int}", async (string id, int index, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).DeleteCardHolderImageAsync(id, index, signature: true, ct);
            return Results.NoContent();
        });
    }

    private static void MapTimeZones(IEndpointRouteBuilder api)
    {
        api.MapGet("/time-zones", async (IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetTimeZonesAsync(ct)));

        api.MapGet("/time-zones/by-name/{name}", async (string name, IWinPakProvider p, CancellationToken ct)
            => await Catalog(p).GetTimeZoneByNameAsync(name, ct) is { } zone
                ? Results.Ok(zone)
                : Results.NotFound());

        api.MapPost("/time-zones", async (UpsertTimeZoneRequest request, IWinPakProvider p, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "Name je povinné." });

            var id = await Catalog(p).AddTimeZoneAsync(request, ct);
            return Results.Created($"/api/v1/time-zones/{id}", new { id });
        });

        api.MapPut("/time-zones/by-name/{name}", async (string name, UpsertTimeZoneRequest request, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).EditTimeZoneAsync(name, request, ct);
            return Results.NoContent();
        });

        api.MapDelete("/time-zones/{id}", async (string id, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).DeleteTimeZoneAsync(id, ct);
            return Results.NoContent();
        });

        api.MapGet("/time-zones/{id}/ranges", async (string id, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetTimeZoneRangesAsync(id, ct)));

        api.MapPut("/time-zones/{id}/ranges", async (string id, List<TimeZoneRangeRequest> ranges, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).ConfigureTimeZoneRangesAsync(id, ranges, ct);
            return Results.NoContent();
        });

        api.MapDelete("/time-zones/{id}/ranges/{rangeId}", async (string id, string rangeId, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).DeleteTimeZoneRangeAsync(id, rangeId, ct);
            return Results.NoContent();
        });
    }

    private static void MapHolidays(IEndpointRouteBuilder api)
    {
        api.MapGet("/holidays/{id}", async (string id, IWinPakProvider p, CancellationToken ct)
            => await Catalog(p).GetHolidayAsync(id, ct) is { } holiday
                ? Results.Ok(holiday)
                : Results.NotFound());

        api.MapPost("/holidays", async (UpsertHolidayRequest request, IWinPakProvider p, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "Name je povinné." });

            var id = await Catalog(p).AddHolidayAsync(request, ct);
            return Results.Created($"/api/v1/holidays/{id}", new { id });
        });

        api.MapPut("/holidays/by-name/{name}", async (string name, UpsertHolidayRequest request, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).EditHolidayAsync(name, request, ct);
            return Results.NoContent();
        });

        api.MapDelete("/holidays/{id}", async (string id, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).DeleteHolidayAsync(id, ct);
            return Results.NoContent();
        });

        api.MapGet("/holiday-groups", async (IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetHolidayGroupsAsync(ct)));

        api.MapGet("/holiday-groups/{id}/holidays", async (string id, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetHolidaysInGroupAsync(id, ct)));

        api.MapPost("/holiday-groups", async (UpsertHolidayGroupRequest request, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).AddHolidayGroupAsync(request, ct);
            return Results.NoContent();
        });

        api.MapPut("/holiday-groups/by-name/{name}", async (string name, UpsertHolidayGroupRequest request, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).EditHolidayGroupAsync(name, request, ct);
            return Results.NoContent();
        });

        api.MapDelete("/holiday-groups/{id}", async (string id, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).DeleteHolidayGroupAsync(id, ct);
            return Results.NoContent();
        });
    }

    private static void MapHardware(IEndpointRouteBuilder api)
    {
        api.MapGet("/hardware", async (IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetHardwareDevicesAsync(ct)));

        api.MapGet("/panels", async (IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetPanelsAsync(ct)));

        api.MapGet("/panels/{panelId:long}/outputs", async (long panelId, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetPanelOutputsAsync(panelId, ct)));

        api.MapGet("/panels/{panelId:long}/groups", async (long panelId, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetPanelGroupsAsync(panelId, ct)));

        api.MapGet("/panels/{panelId:long}/time-zones", async (long panelId, bool? configured, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetPanelTimeZonesAsync(panelId, configured ?? true, ct)));

        api.MapPut("/panels/{panelId:long}/time-zones", async (long panelId, List<string> timeZoneIds, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).ConfigurePanelTimeZonesAsync(panelId, timeZoneIds, ct);
            return Results.NoContent();
        });

        api.MapGet("/panels/{panelId:long}/holiday-groups", async (long panelId, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetPanelHolidayGroupsAsync(panelId, ct)));

        api.MapPut("/panels/{panelId:long}/holiday-groups", async (long panelId, List<string> holidayGroupIds, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).ConfigurePanelHolidayGroupsAsync(panelId, holidayGroupIds, ct);
            return Results.NoContent();
        });

        api.MapPut("/panels/{panelId:long}/outputs/{outputId:long}/time-zone",
            async (long panelId, long outputId, string timeZoneId, int? lockUnlock, IWinPakProvider p, CancellationToken ct) =>
            {
                await Catalog(p).ConfigureOutputTimeZoneAsync(panelId, outputId, timeZoneId, lockUnlock, ct);
                return Results.NoContent();
            });

        api.MapPut("/panels/{panelId:long}/groups/{groupId:long}/time-zone",
            async (long panelId, long groupId, string timeZoneId, IWinPakProvider p, CancellationToken ct) =>
            {
                await Catalog(p).ConfigureGroupTimeZoneAsync(panelId, groupId, timeZoneId, ct);
                return Results.NoContent();
            });

        api.MapGet("/access-areas", async (IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetAccessAreaBranchesAsync(ct)));

        api.MapGet("/access-areas/{branch}/readers", async (string branch, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetReadersInBranchAsync(branch, ct)));

        api.MapGet("/readers/{name}/time-zones", async (string name, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetReaderTimeZonesAsync(name, ct)));

        api.MapGet("/readers/{name}/groups", async (string name, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetReaderGroupsAsync(name, ct)));
    }

    private static void MapSystem(IEndpointRouteBuilder api)
    {
        api.MapGet("/system", async (IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetSystemInfoAsync(ct)));

        api.MapGet("/schedules/{id}", async (string id, IWinPakProvider p, CancellationToken ct)
            => await Catalog(p).GetScheduleAsync(id, ct) is { } schedule
                ? Results.Ok(schedule)
                : Results.NotFound());

        api.MapDelete("/schedules/{id}", async (string id, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).DeleteScheduleAsync(id, ct);
            return Results.NoContent();
        });

        api.MapGet("/templates/{id}", async (string id, IWinPakProvider p, CancellationToken ct)
            => await Catalog(p).GetTemplateAsync(id, ct) is { } template
                ? Results.Ok(template)
                : Results.NotFound());

        api.MapDelete("/templates/{id}", async (string id, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).DeleteTemplateAsync(id, ct);
            return Results.NoContent();
        });

        api.MapGet("/badges/{id}", async (string id, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetBadgeAsync(id, ct)));
    }

    private static void MapCommands(IEndpointRouteBuilder api)
    {
        api.MapPost("/devices/{hid:long}/alarm/acknowledge",
            async (long hid, AlarmPointRequest? request, IWinPakProvider p, CancellationToken ct) =>
            {
                await Catalog(p).AcknowledgeAlarmAsync(hid, request?.Point ?? 0, ct);
                return Results.NoContent();
            });

        api.MapPost("/devices/{hid:long}/alarm/clear",
            async (long hid, AlarmPointRequest? request, IWinPakProvider p, CancellationToken ct) =>
            {
                await Catalog(p).ClearAlarmAsync(hid, request?.Point ?? 0, ct);
                return Results.NoContent();
            });

        api.MapPost("/devices/{hid:long}/note",
            async (long hid, AlarmNoteRequest request, IWinPakProvider p, CancellationToken ct) =>
            {
                await Catalog(p).AddNoteAsync(hid, request.Point, request.Note, ct);
                return Results.NoContent();
            });

        api.MapGet("/devices/{hid:long}/transaction", async (long hid, int? point, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetTransactionDetailsAsync(hid, point ?? 0, ct)));

        api.MapPost("/devices/{hid:long}/shunt", async (long hid, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).ShuntAlarmAsync(hid, shunt: true, ct);
            return Results.NoContent();
        });

        api.MapPost("/devices/{hid:long}/unshunt", async (long hid, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).ShuntAlarmAsync(hid, shunt: false, ct);
            return Results.NoContent();
        });

        api.MapPost("/devices/{hid:long}/buffer", async (long hid, BufferRequest? request, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).BufferAsync(hid, request?.Mode ?? 0, buffer: true, ct);
            return Results.NoContent();
        });

        api.MapPost("/devices/{hid:long}/unbuffer", async (long hid, BufferRequest? request, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).BufferAsync(hid, request?.Mode ?? 0, buffer: false, ct);
            return Results.NoContent();
        });

        api.MapPost("/devices/{hid:long}/energize", async (long hid, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).EnergizeAsync(hid, energize: true, ct);
            return Results.NoContent();
        });

        api.MapPost("/devices/{hid:long}/de-energize", async (long hid, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).EnergizeAsync(hid, energize: false, ct);
            return Results.NoContent();
        });

        api.MapPost("/devices/{hid:long}/restore-time-zone", async (long hid, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).RestoreTimeZoneAsync(hid, ct);
            return Results.NoContent();
        });

        api.MapGet("/devices/{hid:long}/status", async (long hid, int? deviceType, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(new { statusId = await Catalog(p).GetDeviceStatusAsync(hid, deviceType ?? 0, ct) }));

        api.MapPost("/devices/{hid:long}/command", async (long hid, CustomCommandRequest request, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).ExecuteCustomCommandAsync(hid, request.Command, ct);
            return Results.NoContent();
        });

        api.MapPost("/panels/{hid:long}/initialize", async (long hid, PanelInitializeRequest request, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).InitializePanelAsync(hid, request, ct);
            return Results.NoContent();
        });

        api.MapPost("/panels/{hid:long}/cancel-initialize", async (long hid, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).CancelPanelInitializeAsync(hid, ct);
            return Results.NoContent();
        });

        api.MapPost("/panels/{hid:long}/refresh-time-zones", async (long hid, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).RefreshPanelTimeZonesAsync(hid, ct);
            return Results.NoContent();
        });

        api.MapPost("/doors/lock-all", async (long accountId, LockAllDoorsRequest request, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).LockUnlockAllDoorsAsync(accountId, request.Lock, ct);
            return Results.NoContent();
        });

        api.MapPost("/doors/refresh", async (long accountId, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(new { status = await Catalog(p).RefreshDoorsAsync(accountId, ct) }));

        api.MapPost("/doors/schedule", async (DoorScheduleRequest request, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).ExecuteDoorScheduleAsync(request, ct);
            return Results.NoContent();
        });

        api.MapGet("/doors/{hid:long}/netaxs-mode", async (long hid, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetNetAxsDoorModeAsync(hid, ct)));

        api.MapPut("/doors/{hid:long}/netaxs-mode", async (long hid, NetAxsDoorModeDto mode, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).SetNetAxsDoorModeAsync(hid, mode, ct);
            return Results.NoContent();
        });

        api.MapGet("/readers/{hid:long}/default-mode", async (long hid, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(new { mode = await Catalog(p).GetDefaultReaderModeAsync(hid, ct) }));

        api.MapGet("/event-filters", async (bool? commServer, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetEventFiltersAsync(commServer ?? false, ct)));

        api.MapPost("/event-filters/{id:long}", async (long id, bool? commServer, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).AddEventFilterAsync(id, commServer ?? false, ct);
            return Results.NoContent();
        });

        api.MapDelete("/event-filters/{id:long}", async (long id, bool? commServer, IWinPakProvider p, CancellationToken ct) =>
        {
            await Catalog(p).RemoveEventFilterAsync(id, commServer ?? false, ct);
            return Results.NoContent();
        });

        api.MapGet("/muster", async (long areaId, long accountId, int? sortField, int? sortOrder, IWinPakProvider p, CancellationToken ct)
            => Results.Ok(await Catalog(p).GetMusterAsync(areaId, accountId, sortField ?? 0, sortOrder ?? 0, ct)));
    }
}
