using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers;

/// <summary>
/// Rozšířená část WIN-PAK API, kterou hlavní ACS aplikace pro schvalování
/// nepotřebuje, ale konektor ji zpřístupňuje pro správu systému: číselníky
/// (časové zóny, svátky), konfiguraci hardwaru, hromadné operace s kartami
/// a plnou správu přístupových úrovní.
///
/// Provider, který ji neimplementuje, vrací na těchto endpointech 501.
/// </summary>
public interface IWinPakCatalogApi
{
    // ---------- Přístupové úrovně ----------

    Task<AccessLevelDto?> GetAccessLevelByNameAsync(string name, CancellationToken ct);

    Task<string?> GetAccessTreeAsync(string accessLevelName, CancellationToken ct);

    Task CreateAccessLevelAsync(CreateAccessLevelRequest request, CancellationToken ct);

    Task UpsertAccessLevelAsync(string? accessLevelId, UpsertAccessLevelRequest request, CancellationToken ct);

    Task ConfigureAccessLevelAsync(string accessLevelName, ConfigureAccessLevelRequest request, CancellationToken ct);

    Task ConfigureEntranceAccessAsync(string accessLevelName, ConfigureEntranceRequest request, CancellationToken ct);

    Task DeleteAccessLevelAsync(string accessLevelName, CancellationToken ct);

    Task<IReadOnlyList<CardDto>> IsolateAccessLevelAsync(string accessLevelName, CancellationToken ct);

    Task<IReadOnlyList<AccessLevelDto>> GetAccessLevelsForReassignAsync(string accessLevelName, CancellationToken ct);

    Task ReassignAccessLevelAsync(string accessLevelName, ReassignAccessLevelRequest request, CancellationToken ct);

    // ---------- Karty ----------

    Task<IReadOnlyList<CardDto>> GetCardsAsync(bool onlyWithoutHolder, CancellationToken ct);

    Task BulkAddCardsAsync(BulkAddCardsRequest request, CancellationToken ct);

    Task BulkDeleteCardsAsync(BulkDeleteCardsRequest request, CancellationToken ct);

    // ---------- Držitelé ----------

    Task DeleteCardHolderAsync(string id, DeleteCardHolderOptions options, CancellationToken ct);

    Task<IReadOnlyList<CardHolderSearchFieldDto>> GetCardHolderSearchFieldsAsync(CancellationToken ct);

    Task<IReadOnlyList<CardHolderDto>> SearchCardHoldersAsync(CardHolderSearchRequest request, CancellationToken ct);

    Task<IReadOnlyList<NoteFieldTemplateDto>> GetNoteFieldTemplatesAsync(CancellationToken ct);

    Task<CardHolderImageDto> GetCardHolderImageAsync(string id, int index, bool signature, CancellationToken ct);

    Task ImportCardHolderImageAsync(string id, int index, bool signature, string contentBase64, CancellationToken ct);

    Task DeleteCardHolderImageAsync(string id, int index, bool signature, CancellationToken ct);

    // ---------- Časové zóny ----------

    Task<IReadOnlyList<TimeZoneDto>> GetTimeZonesAsync(CancellationToken ct);

    Task<TimeZoneDto?> GetTimeZoneByNameAsync(string name, CancellationToken ct);

    Task<string> AddTimeZoneAsync(UpsertTimeZoneRequest request, CancellationToken ct);

    Task EditTimeZoneAsync(string currentName, UpsertTimeZoneRequest request, CancellationToken ct);

    Task DeleteTimeZoneAsync(string timeZoneId, CancellationToken ct);

    Task<IReadOnlyList<TimeZoneRangeDto>> GetTimeZoneRangesAsync(string timeZoneId, CancellationToken ct);

    Task ConfigureTimeZoneRangesAsync(string timeZoneId, IReadOnlyList<TimeZoneRangeRequest> ranges, CancellationToken ct);

    Task DeleteTimeZoneRangeAsync(string timeZoneId, string rangeId, CancellationToken ct);

    // ---------- Svátky ----------

    Task<HolidayDto?> GetHolidayAsync(string holidayId, CancellationToken ct);

    Task<string> AddHolidayAsync(UpsertHolidayRequest request, CancellationToken ct);

    Task EditHolidayAsync(string currentName, UpsertHolidayRequest request, CancellationToken ct);

    Task DeleteHolidayAsync(string holidayId, CancellationToken ct);

    Task<IReadOnlyList<HolidayGroupDto>> GetHolidayGroupsAsync(CancellationToken ct);

    Task<IReadOnlyList<HolidayDto>> GetHolidaysInGroupAsync(string holidayGroupId, CancellationToken ct);

    Task AddHolidayGroupAsync(UpsertHolidayGroupRequest request, CancellationToken ct);

    Task EditHolidayGroupAsync(string currentName, UpsertHolidayGroupRequest request, CancellationToken ct);

    Task DeleteHolidayGroupAsync(string holidayGroupId, CancellationToken ct);

    // ---------- Hardware ----------

    Task<IReadOnlyList<HardwareDeviceDto>> GetHardwareDevicesAsync(CancellationToken ct);

    Task<IReadOnlyList<PanelDto>> GetPanelsAsync(CancellationToken ct);

    Task<IReadOnlyList<PanelPointDto>> GetPanelOutputsAsync(long panelId, CancellationToken ct);

    Task<IReadOnlyList<PanelPointDto>> GetPanelGroupsAsync(long panelId, CancellationToken ct);

    Task<IReadOnlyList<TimeZoneDto>> GetPanelTimeZonesAsync(long panelId, bool configured, CancellationToken ct);

    Task ConfigurePanelTimeZonesAsync(long panelId, IReadOnlyList<string> timeZoneIds, CancellationToken ct);

    Task<IReadOnlyList<HolidayGroupDto>> GetPanelHolidayGroupsAsync(long panelId, CancellationToken ct);

    Task ConfigurePanelHolidayGroupsAsync(long panelId, IReadOnlyList<string> holidayGroupIds, CancellationToken ct);

    Task ConfigureOutputTimeZoneAsync(long panelId, long outputId, string timeZoneId, int? lockUnlock, CancellationToken ct);

    Task ConfigureGroupTimeZoneAsync(long panelId, long groupId, string timeZoneId, CancellationToken ct);

    Task<IReadOnlyList<AccessAreaBranchDto>> GetAccessAreaBranchesAsync(CancellationToken ct);

    Task<IReadOnlyList<ReaderDto>> GetReadersInBranchAsync(string branchName, CancellationToken ct);

    Task<IReadOnlyList<TimeZoneDto>> GetReaderTimeZonesAsync(string readerName, CancellationToken ct);

    Task<IReadOnlyList<PanelPointDto>> GetReaderGroupsAsync(string readerName, CancellationToken ct);

    // ---------- Systém ----------

    Task<SystemInfoDto> GetSystemInfoAsync(CancellationToken ct);

    Task<ScheduleDto?> GetScheduleAsync(string scheduleId, CancellationToken ct);

    Task DeleteScheduleAsync(string scheduleId, CancellationToken ct);

    Task<TemplateDto?> GetTemplateAsync(string templateId, CancellationToken ct);

    Task DeleteTemplateAsync(string templateId, CancellationToken ct);

    Task<BadgeDto> GetBadgeAsync(string badgeId, CancellationToken ct);

    // ---------- Komunikační server ----------

    Task AcknowledgeAlarmAsync(long hid, int point, CancellationToken ct);

    Task ClearAlarmAsync(long hid, int point, CancellationToken ct);

    Task AddNoteAsync(long hid, int point, string note, CancellationToken ct);

    Task<TransactionDetailDto> GetTransactionDetailsAsync(long hid, int point, CancellationToken ct);

    Task ShuntAlarmAsync(long hid, bool shunt, CancellationToken ct);

    Task BufferAsync(long hid, int mode, bool buffer, CancellationToken ct);

    Task EnergizeAsync(long hid, bool energize, CancellationToken ct);

    Task RestoreTimeZoneAsync(long hid, CancellationToken ct);

    Task InitializePanelAsync(long hid, PanelInitializeRequest request, CancellationToken ct);

    Task CancelPanelInitializeAsync(long hid, CancellationToken ct);

    Task RefreshPanelTimeZonesAsync(long hid, CancellationToken ct);

    Task LockUnlockAllDoorsAsync(long accountId, bool shouldLock, CancellationToken ct);

    Task<int> RefreshDoorsAsync(long accountId, CancellationToken ct);

    Task ExecuteDoorScheduleAsync(DoorScheduleRequest request, CancellationToken ct);

    Task<NetAxsDoorModeDto> GetNetAxsDoorModeAsync(long hid, CancellationToken ct);

    Task SetNetAxsDoorModeAsync(long hid, NetAxsDoorModeDto mode, CancellationToken ct);

    Task<int> GetDeviceStatusAsync(long hid, int deviceType, CancellationToken ct);

    Task<int> GetDefaultReaderModeAsync(long hid, CancellationToken ct);

    Task<IReadOnlyList<string>> GetEventFiltersAsync(bool commServer, CancellationToken ct);

    Task AddEventFilterAsync(long id, bool commServer, CancellationToken ct);

    Task RemoveEventFilterAsync(long id, bool commServer, CancellationToken ct);

    Task<MusterElementDto> GetMusterAsync(long areaId, long accountId, int sortField, int sortOrder, CancellationToken ct);

    Task ExecuteCustomCommandAsync(long hid, string command, CancellationToken ct);
}
