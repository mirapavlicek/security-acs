using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers.Com;

/// <summary>
/// Rozšířená část API. Každá metoda je tenké přemostění na obálku COM volání —
/// veškerá logika mapování je v <see cref="WinPakDatabaseApi"/> a <see cref="WinPakCommApi"/>.
/// </summary>
public sealed partial class ComWinPakProvider : IWinPakCatalogApi
{
    // ---------- Přístupové úrovně ----------

    public Task<AccessLevelDto?> GetAccessLevelByNameAsync(string name, CancellationToken ct)
        => RunAsync(() => _database.GetAccessLevelByName(name), ct);

    public Task<string?> GetAccessTreeAsync(string accessLevelName, CancellationToken ct)
        => RunAsync(() => _database.GetAccessTree(accessLevelName), ct);

    public Task CreateAccessLevelAsync(CreateAccessLevelRequest request, CancellationToken ct)
        => RunAsync(() => _database.CreateAccessLevel(request), ct);

    public Task UpsertAccessLevelAsync(string? accessLevelId, UpsertAccessLevelRequest request, CancellationToken ct)
        => RunAsync(() => _database.UpsertAccessLevel(accessLevelId, request), ct);

    public Task ConfigureAccessLevelAsync(string accessLevelName, ConfigureAccessLevelRequest request, CancellationToken ct)
        => RunAsync(() => _database.ConfigureAccessLevel(accessLevelName, request), ct);

    public Task ConfigureEntranceAccessAsync(string accessLevelName, ConfigureEntranceRequest request, CancellationToken ct)
        => RunAsync(() => _database.ConfigureEntranceAccess(accessLevelName, request), ct);

    public Task DeleteAccessLevelAsync(string accessLevelName, CancellationToken ct)
        => RunAsyncInvalidating(() => _database.DeleteAccessLevel(accessLevelName), ct);

    public Task<IReadOnlyList<CardDto>> IsolateAccessLevelAsync(string accessLevelName, CancellationToken ct)
        => RunAsync(() => _database.IsolateAccessLevel(accessLevelName), ct);

    public Task<IReadOnlyList<AccessLevelDto>> GetAccessLevelsForReassignAsync(string accessLevelName, CancellationToken ct)
        => RunAsync(() => _database.GetAccessLevelsForReassign(accessLevelName), ct);

    public Task ReassignAccessLevelAsync(string accessLevelName, ReassignAccessLevelRequest request, CancellationToken ct)
        => RunAsync(() => _database.ReassignAccessLevel(accessLevelName, request), ct);

    public Task AddAccessLevelAsync(CreateAccessLevelRequest request, CancellationToken ct)
        => RunAsyncInvalidating(() => _database.AddAccessLevel(request), ct);

    public Task EditAccessLevelAsync(string currentName, CreateAccessLevelRequest request, CancellationToken ct)
        => RunAsync(() => _database.EditAccessLevel(currentName, request), ct);

    // ---------- Účty ----------

    public Task<AccountDto?> GetAccountAsync(string accountId, CancellationToken ct)
        => RunAsync(() => _database.GetAccount(accountId), ct);

    // ---------- Karty ----------

    public Task<IReadOnlyList<CardDto>> GetCardsAsync(bool onlyWithoutHolder, CancellationToken ct)
        => RunAsync(() => onlyWithoutHolder ? _database.GetCardsWithoutHolder() : _database.GetCards(), ct);

    public Task UpsertCardExAsync(string cardNumber, UpsertCardExRequest request, CancellationToken ct)
        => RunAsync(() =>
        {
            var (accountId, subAccountId) = _database.ResolveAccountIds();
            _database.UpsertCardEx(cardNumber, request.Card, request.NetAxs, accountId, subAccountId);
        }, ct);

    public Task BulkAddCardsAsync(BulkAddCardsRequest request, CancellationToken ct)
        => RunAsync(() => _database.BulkAddCards(request), ct);

    public Task BulkDeleteCardsAsync(BulkDeleteCardsRequest request, CancellationToken ct)
        => RunAsync(() => _database.BulkDeleteCards(request), ct);

    // ---------- Držitelé ----------

    public Task DeleteCardHolderAsync(string id, DeleteCardHolderOptions options, CancellationToken ct)
        => RunAsync(() => _database.DeleteCardHolder(id, options), ct);

    public Task<IReadOnlyList<CardHolderSearchFieldDto>> GetCardHolderSearchFieldsAsync(CancellationToken ct)
        => RunAsync(_database.GetCardHolderSearchFields, ct);

    public Task<IReadOnlyList<CardHolderDto>> SearchCardHoldersAsync(CardHolderSearchRequest request, CancellationToken ct)
        => RunAsync(() => _database.SearchCardHolders(request), ct);

    public Task<IReadOnlyList<NoteFieldTemplateDto>> GetNoteFieldTemplatesAsync(CancellationToken ct)
        => RunAsync(_database.GetNoteFieldTemplates, ct);

    public Task<CardHolderImageDto> GetCardHolderImageAsync(string id, int index, bool signature, CancellationToken ct)
        => RunAsync(() => signature ? _database.GetSignature(id, index) : _database.GetPhoto(id, index), ct);

    public Task ImportCardHolderImageAsync(string id, int index, bool signature, string contentBase64, CancellationToken ct)
        => RunAsync(() =>
        {
            if (signature)
                _database.ImportSignature(id, index, contentBase64);
            else
                _database.ImportPhoto(id, index, contentBase64);
        }, ct);

    public Task DeleteCardHolderImageAsync(string id, int index, bool signature, bool shortVariant, CancellationToken ct)
        => RunAsync(() =>
        {
            if (!signature)
                _database.DeletePhoto(id, index);
            else if (shortVariant)
                _database.DeleteSignatureShort(id, index);
            else
                _database.DeleteSignature(id, index);
        }, ct);

    // ---------- Časové zóny ----------

    public Task<IReadOnlyList<TimeZoneDto>> GetTimeZonesAsync(CancellationToken ct)
        => RunAsync(() => Cached("time-zones", _database.GetTimeZones), ct);

    public Task<TimeZoneDto?> GetTimeZoneByNameAsync(string name, CancellationToken ct)
        => RunAsync(() => _database.GetTimeZoneByName(name), ct);

    public Task<string> AddTimeZoneAsync(UpsertTimeZoneRequest request, CancellationToken ct)
        => RunAsyncInvalidating(() => _database.AddTimeZone(request), ct);

    public Task CreateTimeZoneAsync(UpsertTimeZoneRequest request, CancellationToken ct)
        => RunAsync(() => _database.CreateTimeZone(request), ct);

    public Task EditTimeZoneAsync(string currentName, UpsertTimeZoneRequest request, CancellationToken ct)
        => RunAsync(() => _database.EditTimeZone(currentName, request), ct);

    public Task DeleteTimeZoneAsync(string timeZoneId, CancellationToken ct)
        => RunAsyncInvalidating(() => _database.DeleteTimeZone(timeZoneId), ct);

    public Task<IReadOnlyList<TimeZoneRangeDto>> GetTimeZoneRangesAsync(string timeZoneId, CancellationToken ct)
        => RunAsync(() => _database.GetTimeZoneRanges(timeZoneId), ct);

    public Task ConfigureTimeZoneRangesAsync(string timeZoneId, IReadOnlyList<TimeZoneRangeRequest> ranges, CancellationToken ct)
        => RunAsync(() => _database.ConfigureTimeZoneRanges(timeZoneId, ranges), ct);

    public Task DeleteTimeZoneRangeAsync(string timeZoneId, string rangeId, CancellationToken ct)
        => RunAsync(() => _database.DeleteTimeZoneRange(timeZoneId, rangeId), ct);

    public Task<TimeZoneUsageDto> GetTimeZoneUsageAsync(string timeZoneId, CancellationToken ct)
        => RunAsync(() => _database.GetTimeZoneUsage(timeZoneId), ct);

    public Task<IReadOnlyList<TimeZoneDto>> GetTimeZonesForReassignAsync(string timeZoneId, bool forOperators, CancellationToken ct)
        => RunAsync(() => _database.GetTimeZonesForReassign(timeZoneId, forOperators), ct);

    public Task ReassignTimeZoneAsync(ReassignTimeZoneRequest request, CancellationToken ct)
        => RunAsync(() => _database.ReassignTimeZone(request), ct);

    public Task DeletePanelTimeZoneAsync(string timeZoneId, IReadOnlyList<string> panelIds, CancellationToken ct)
        => RunAsync(() => _database.DeletePanelTimeZone(timeZoneId, panelIds), ct);

    // ---------- Svátky ----------

    public Task<HolidayDto?> GetHolidayAsync(string holidayId, CancellationToken ct)
        => RunAsync(() => _database.GetHoliday(holidayId), ct);

    public Task<string> AddHolidayAsync(UpsertHolidayRequest request, CancellationToken ct)
        => RunAsync(() => _database.AddHoliday(request), ct);

    public Task EditHolidayAsync(string currentName, UpsertHolidayRequest request, CancellationToken ct)
        => RunAsync(() => _database.EditHoliday(currentName, request), ct);

    public Task DeleteHolidayAsync(string holidayId, CancellationToken ct)
        => RunAsync(() => _database.DeleteHoliday(holidayId), ct);

    public Task<IReadOnlyList<HolidayGroupDto>> GetHolidayGroupsAsync(CancellationToken ct)
        => RunAsync(_database.GetHolidayGroups, ct);

    public Task<IReadOnlyList<HolidayDto>> GetHolidaysInGroupAsync(string holidayGroupId, CancellationToken ct)
        => RunAsync(() => _database.GetHolidaysInGroup(holidayGroupId), ct);

    public Task AddHolidayGroupAsync(UpsertHolidayGroupRequest request, CancellationToken ct)
        => RunAsync(() => _database.AddHolidayGroup(request), ct);

    public Task EditHolidayGroupAsync(string currentName, UpsertHolidayGroupRequest request, CancellationToken ct)
        => RunAsync(() => _database.EditHolidayGroup(currentName, request), ct);

    public Task DeleteHolidayGroupAsync(string holidayGroupId, CancellationToken ct)
        => RunAsync(() => _database.DeleteHolidayGroup(holidayGroupId), ct);

    // ---------- Hardware ----------

    public Task<IReadOnlyList<HardwareDeviceDto>> GetHardwareDevicesAsync(CancellationToken ct)
        => RunAsync(() => Cached("hardware", _database.GetHardwareDevices), ct);

    public Task<IReadOnlyList<PanelDto>> GetPanelsAsync(CancellationToken ct)
        => RunAsync(() => Cached("panels", _database.GetPanels), ct);

    public Task<IReadOnlyList<PanelPointDto>> GetPanelOutputsAsync(long panelId, CancellationToken ct)
        => RunAsync(() => _database.GetPanelOutputs(panelId), ct);

    public Task<IReadOnlyList<PanelPointDto>> GetPanelGroupsAsync(long panelId, CancellationToken ct)
        => RunAsync(() => _database.GetPanelGroups(panelId), ct);

    public Task<IReadOnlyList<TimeZoneDto>> GetPanelTimeZonesAsync(long panelId, bool configured, CancellationToken ct)
        => RunAsync(() => configured
            ? _database.GetConfiguredTimeZonesOfPanel(panelId)
            : _database.GetAvailableTimeZonesOfPanel(panelId), ct);

    public Task ConfigurePanelTimeZonesAsync(long panelId, IReadOnlyList<string> timeZoneIds, CancellationToken ct)
        => RunAsync(() => _database.ConfigurePanelTimeZones(panelId, timeZoneIds), ct);

    public Task<IReadOnlyList<HolidayGroupDto>> GetPanelHolidayGroupsAsync(long panelId, CancellationToken ct)
        => RunAsync(() => _database.GetConfiguredHolidayGroupsOfPanel(panelId), ct);

    public Task ConfigurePanelHolidayGroupsAsync(long panelId, IReadOnlyList<string> holidayGroupIds, CancellationToken ct)
        => RunAsync(() => _database.ConfigurePanelHolidayGroups(panelId, holidayGroupIds), ct);

    public Task ConfigureOutputTimeZoneAsync(long panelId, long outputId, string timeZoneId, int? lockUnlock, CancellationToken ct)
        => RunAsync(() =>
        {
            if (lockUnlock is { } value)
                _database.ConfigureOutputTimeZoneEx(panelId, outputId, value, timeZoneId);
            else
                _database.ConfigureOutputTimeZone(panelId, outputId, timeZoneId);
        }, ct);

    public Task ConfigureGroupTimeZoneAsync(long panelId, long groupId, string timeZoneId, CancellationToken ct)
        => RunAsync(() => _database.ConfigureGroupTimeZone(panelId, groupId, timeZoneId), ct);

    public Task<IReadOnlyList<AccessAreaBranchDto>> GetAccessAreaBranchesAsync(CancellationToken ct)
        => RunAsync(_database.GetAccessAreaBranches, ct);

    public Task<IReadOnlyList<ReaderDto>> GetReadersInBranchAsync(string branchName, CancellationToken ct)
        => RunAsync(() => _database.GetReadersInAccessAreaBranch(branchName), ct);

    public Task<IReadOnlyList<TimeZoneDto>> GetReaderTimeZonesAsync(string readerName, bool forAccount, CancellationToken ct)
        => RunAsync(() => forAccount
            ? _database.GetAvailableTimeZonesOfAccessReader(readerName)
            : _database.GetAvailableTimeZonesOfReader(readerName), ct);

    public Task<IReadOnlyList<TimeZoneDto>> GetBranchTimeZonesAsync(string branchName, CancellationToken ct)
        => RunAsync(() => _database.GetAvailableTimeZonesOfBranch(branchName), ct);

    public Task<string?> GetAssociatedGroupAsync(string accessLevelName, string readerName, CancellationToken ct)
        => RunAsync(() => _database.GetAssociatedGroupOfReader(accessLevelName, readerName), ct);

    public Task<IReadOnlyList<PanelPointDto>> GetReaderGroupsAsync(string readerName, CancellationToken ct)
        => RunAsync(() => _database.GetAvailableGroupsOfReader(readerName), ct);

    // ---------- Systém ----------

    public Task<SystemInfoDto> GetSystemInfoAsync(CancellationToken ct)
        => RunAsync(_database.GetSystemInfo, ct);

    public Task<ScheduleDto?> GetScheduleAsync(string scheduleId, CancellationToken ct)
        => RunAsync(() => _database.GetSchedule(scheduleId), ct);

    public Task UpsertScheduleAsync(ScheduleDto schedule, CancellationToken ct)
        => RunAsync(() => _database.UpsertSchedule(schedule), ct);

    public Task DeleteScheduleAsync(string scheduleId, CancellationToken ct)
        => RunAsync(() => _database.DeleteSchedule(scheduleId), ct);

    public Task<TemplateDto?> GetTemplateAsync(string templateId, CancellationToken ct)
        => RunAsync(() => _database.GetTemplate(templateId), ct);

    public Task UpsertTemplateAsync(TemplateDto template, CancellationToken ct)
        => RunAsync(() => _database.UpsertTemplate(template), ct);

    public Task DeleteTemplateAsync(string templateId, CancellationToken ct)
        => RunAsync(() => _database.DeleteTemplate(templateId), ct);

    public Task<BadgeDto> GetBadgeAsync(string badgeId, CancellationToken ct)
        => RunAsync(() => _database.GetBadge(badgeId), ct);

    public Task<LookupResultDto> LookupAsync(LookupKind kind, string value, CancellationToken ct)
        => RunAsync(() => new LookupResultDto(kind, value, kind switch
        {
            LookupKind.DeviceName => _database.GetDeviceName(ComValue.ToLong(value)),
            LookupKind.AccountByDevice => _database.GetAccountIdByHid(ComValue.ToLong(value)),
            LookupKind.AccessLevelName => _database.GetAccessLevelName(value),
            LookupKind.TimeZoneName => _database.GetTimeZoneName(value),
            LookupKind.AccountName => _database.GetAccountName(value),
            LookupKind.SubAccountName => _database.GetSubAccountName(value),
            LookupKind.AccountEmails => _database.GetAccountEmails(),
            LookupKind.ReaderTimeZoneDetails => _database.GetReaderTimeZoneDetails(),
            LookupKind.LoopTimeZones => _database.GetLoopTimeZones(),
            LookupKind.ReaderDirectPoint => Join(_database.GetDirectPointTimeZoneOfReader(ComValue.ToLong(value))),
            LookupKind.PanelGroupCheck => _database.IsPanelGroupChecked(ComValue.ToLong(value)).ToString(),
            _ => null,
        }), ct);

    private static string Join((string? DeviceId, string? TimeZoneId) point)
        => $"{point.DeviceId}/{point.TimeZoneId}";

    public Task<TimeZoneDto?> GetAssociatedTimeZoneAsync(AssociatedTimeZoneQuery query, CancellationToken ct)
        => RunAsync(() => query switch
        {
            { AccessLevelName: { } level, ReaderName: { } reader }
                => _database.GetAssociatedTimeZoneOfReader(level, reader),
            { PanelId: { } panel, OutputId: { } output, LockUnlock: { } lockUnlock }
                => _database.GetAssociatedTimeZoneOfOutputEx(panel, output, lockUnlock),
            { PanelId: { } panel, OutputId: { } output }
                => _database.GetAssociatedTimeZoneOfOutput(panel, output),
            { PanelId: { } panel, GroupId: { } group }
                => _database.GetAssociatedTimeZoneOfGroup(panel, group),
            _ => throw new ArgumentException(
                "Zadejte buď přístupovou úroveň a čtečku, nebo panel a výstup či skupinu."),
        }, ct);

    public Task<IReadOnlyList<PanelDto>> GetPanelsUsingHolidayGroupAsync(string holidayGroupId, CancellationToken ct)
        => RunAsync(() => _database.IsolatePanelsForHolidayGroupDelete(holidayGroupId), ct);

    public Task DeleteAccessLevelWithReplacementAsync(string accessLevelId, string replacementId, bool multiple, CancellationToken ct)
        => RunAsync(() => _database.DeleteAccessLevelWithReplacement(accessLevelId, replacementId, multiple), ct);

    public Task WriteCardObjectAsync(string cardNumber, UpsertCardRequest request, bool edit, CancellationToken ct)
        => RunAsync(() =>
        {
            if (edit)
                _database.EditCard(cardNumber, request);
            else
                _database.AddCard(cardNumber, request);
        }, ct);

    // ---------- Komunikační server ----------

    public Task AcknowledgeAlarmAsync(long hid, int point, CancellationToken ct)
        => RunAsync(() => Comm.AcknowledgeAlarm(hid, point), ct);

    public Task ClearAlarmAsync(long hid, int point, CancellationToken ct)
        => RunAsync(() => Comm.ClearAlarm(hid, point), ct);

    public Task AddNoteAsync(long hid, int point, string note, CancellationToken ct)
        => RunAsync(() => Comm.AddNote(hid, point, note), ct);

    public Task<TransactionDetailDto> GetTransactionDetailsAsync(long hid, int point, CancellationToken ct)
        => RunAsync(() => Comm.GetTransactionDetails(hid, point), ct);

    public Task ShuntAlarmAsync(long hid, bool shunt, CancellationToken ct)
        => RunAsync(() =>
        {
            if (shunt)
                Comm.ShuntAlarm(hid);
            else
                Comm.UnshuntAlarm(hid);
        }, ct);

    public Task LockEntryPointAsync(long hid, int point, bool unlock, CancellationToken ct)
        => RunAsync(() =>
        {
            if (unlock)
                Comm.UnlockEntryPoint(hid, point);
            else
                Comm.LockEntryPoint(hid, point);
        }, ct);

    public Task UnshuntAlarmPointAsync(long hid, int point, CancellationToken ct)
        => RunAsync(() => Comm.UnshuntAlarmPoint(hid, point), ct);

    public Task<int> GetDoorStatusCodeAsync(long hid, CancellationToken ct)
        => RunAsync(() => Comm.GetDoorStatusCode(hid), ct);

    public Task BufferAsync(long hid, int mode, bool buffer, CancellationToken ct)
        => RunAsync(() =>
        {
            if (buffer)
                Comm.Buffer(hid, mode);
            else
                Comm.Unbuffer(hid, mode);
        }, ct);

    public Task EnergizeAsync(long hid, bool energize, CancellationToken ct)
        => RunAsync(() =>
        {
            if (energize)
                Comm.Energize(hid);
            else
                Comm.DeEnergize(hid);
        }, ct);

    public Task RestoreTimeZoneAsync(long hid, CancellationToken ct)
        => RunAsync(() => Comm.RestoreTimeZone(hid), ct);

    public Task InitializePanelAsync(long hid, PanelInitializeRequest request, CancellationToken ct)
        => RunAsync(() => Comm.InitializePanel(hid, request.PanelType, request.Tasks), ct);

    public Task CancelPanelInitializeAsync(long hid, CancellationToken ct)
        => RunAsync(() => Comm.CancelPanelInitialize(hid), ct);

    public Task RefreshPanelTimeZonesAsync(long hid, CancellationToken ct)
        => RunAsync(() => Comm.RefreshPanelTimeZones(hid), ct);

    public Task LockUnlockAllDoorsAsync(long accountId, bool shouldLock, CancellationToken ct)
        => RunAsync(() => Comm.LockUnlockAllDoors(accountId, shouldLock), ct);

    public Task<int> RefreshDoorsAsync(long accountId, CancellationToken ct)
        => RunAsync(() => Comm.RefreshDoors(accountId), ct);

    public Task ExecuteDoorScheduleAsync(DoorScheduleRequest request, CancellationToken ct)
        => RunAsync(() => Comm.ExecuteDoorSchedule(request), ct);

    public Task<NetAxsDoorModeDto> GetNetAxsDoorModeAsync(long hid, CancellationToken ct)
        => RunAsync(() => Comm.GetNetAxsDoorMode(hid), ct);

    public Task SetNetAxsDoorModeAsync(long hid, NetAxsDoorModeDto mode, CancellationToken ct)
        => RunAsync(() => Comm.SetNetAxsDoorMode(hid, mode), ct);

    public Task<int> GetDeviceStatusAsync(long hid, int deviceType, CancellationToken ct)
        => RunAsync(() => Comm.GetDeviceStatus(hid, deviceType), ct);

    public Task<int> GetDefaultReaderModeAsync(long hid, CancellationToken ct)
        => RunAsync(() => Comm.GetDefaultReaderMode(hid), ct);

    public Task<IReadOnlyList<string>> GetEventFiltersAsync(bool commServer, CancellationToken ct)
        => RunAsync(() => commServer ? Comm.GetCommServerFilters() : Comm.GetEventFilters(), ct);

    public Task AddEventFilterAsync(long id, bool commServer, CancellationToken ct)
        => RunAsync(() =>
        {
            if (commServer)
                Comm.AddCommServerFilter(id);
            else
                Comm.AddEventFilter(id);
        }, ct);

    public Task RemoveEventFilterAsync(long id, bool commServer, CancellationToken ct)
        => RunAsync(() =>
        {
            if (commServer)
                Comm.RemoveCommServerFilter(id);
            else
                Comm.RemoveEventFilter(id);
        }, ct);

    public Task<MusterElementDto> GetMusterAsync(long areaId, long accountId, int sortField, int sortOrder, CancellationToken ct)
        => RunAsync(() => Comm.GetMusterElements(areaId, accountId, sortField, sortOrder), ct);

    public Task ExecuteCustomCommandAsync(long hid, string command, CancellationToken ct)
        => RunAsync(() => Comm.ExecuteCustomCommand(hid, command), ct);
}
