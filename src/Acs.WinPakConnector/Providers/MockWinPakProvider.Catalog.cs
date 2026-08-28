using System.Collections.Concurrent;
using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers;

/// <summary>
/// Rozšířená část API pro vývoj bez WIN-PAKu. Číselníky se drží v paměti,
/// konfigurační povely se jen zaznamenají, aby šlo ověřit, že je REST volá.
/// </summary>
public sealed partial class MockWinPakProvider : IWinPakCatalogApi
{
    private readonly ConcurrentDictionary<string, TimeZoneDto> _timeZones = new(
        new Dictionary<string, TimeZoneDto>
        {
            ["1"] = new("1", "Nepřetržitě", "24/7", "FNMH"),
            ["2"] = new("2", "Pracovní doba", "Po–Pá 6:00–18:00", "FNMH"),
        });

    private readonly ConcurrentDictionary<string, List<TimeZoneRangeDto>> _timeZoneRanges = new();
    private readonly ConcurrentDictionary<string, HolidayDto> _holidays = new(
        new Dictionary<string, HolidayDto>
        {
            ["1"] = new("1", "Nový rok", 2026, 1, 1, 0, true),
        });

    private readonly ConcurrentDictionary<string, HolidayGroupDto> _holidayGroups = new(
        new Dictionary<string, HolidayGroupDto>
        {
            ["1"] = new("1", "Státní svátky", "1"),
        });

    private readonly ConcurrentDictionary<string, List<string>> _holidayGroupMembers = new();
    private readonly ConcurrentDictionary<long, List<string>> _panelTimeZones = new();
    private readonly ConcurrentDictionary<long, List<string>> _panelHolidayGroups = new();
    private readonly ConcurrentDictionary<string, string> _cardHolderImages = new();
    private readonly ConcurrentDictionary<long, NetAxsDoorModeDto> _netAxsModes = new();
    private readonly List<string> _eventFilters = [];
    private readonly List<string> _commServerFilters = [];

    private int _nextTimeZoneId = 100;
    private int _nextHolidayId = 100;

    /// <summary>Povely bez návratové hodnoty se zaznamenávají, aby šly ověřit v testech.</summary>
    public List<string> ExecutedCommands { get; } = [];

    private Task Record(string command)
    {
        lock (ExecutedCommands)
            ExecutedCommands.Add(command);
        return Task.CompletedTask;
    }

    private Task<T> Record<T>(string command, T result)
    {
        lock (ExecutedCommands)
            ExecutedCommands.Add(command);
        return Task.FromResult(result);
    }

    // ---------- Přístupové úrovně ----------

    public Task<AccessLevelDto?> GetAccessLevelByNameAsync(string name, CancellationToken ct)
        => Task.FromResult(AccessLevels.FirstOrDefault(al =>
            al.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

    public Task<string?> GetAccessTreeAsync(string accessLevelName, CancellationToken ct)
        => Task.FromResult<string?>($"<AccessTree><AccessLevel>{accessLevelName}</AccessLevel></AccessTree>");

    public Task CreateAccessLevelAsync(CreateAccessLevelRequest request, CancellationToken ct)
        => Record($"create-access-level:{request.Name}");

    public Task UpsertAccessLevelAsync(string? accessLevelId, UpsertAccessLevelRequest request, CancellationToken ct)
        => Record($"upsert-access-level:{accessLevelId}:{request.Name}");

    public Task ConfigureAccessLevelAsync(string accessLevelName, ConfigureAccessLevelRequest request, CancellationToken ct)
        => Record($"configure-access-level:{accessLevelName}:{string.Join('|', request.ReaderNames)}:{request.TimeZoneName}");

    public Task ConfigureEntranceAccessAsync(string accessLevelName, ConfigureEntranceRequest request, CancellationToken ct)
        => Record($"configure-entrance:{accessLevelName}:{request.ReaderName}");

    public Task DeleteAccessLevelAsync(string accessLevelName, CancellationToken ct)
        => Record($"delete-access-level:{accessLevelName}");

    public Task<IReadOnlyList<CardDto>> IsolateAccessLevelAsync(string accessLevelName, CancellationToken ct)
    {
        var level = AccessLevels.FirstOrDefault(al => al.Name.Equals(accessLevelName, StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<CardDto> cards = level is null
            ? []
            : _cardHolders.Values.SelectMany(h => h.Cards)
                .Where(c => c.AccessLevelIds.Contains(level.Id))
                .ToList();
        return Task.FromResult(cards);
    }

    public Task<IReadOnlyList<AccessLevelDto>> GetAccessLevelsForReassignAsync(string accessLevelName, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AccessLevelDto>>(
            AccessLevels.Where(al => !al.Name.Equals(accessLevelName, StringComparison.OrdinalIgnoreCase)).ToList());

    public Task ReassignAccessLevelAsync(string accessLevelName, ReassignAccessLevelRequest request, CancellationToken ct)
        => Record($"reassign-access-level:{accessLevelName}->{request.NewAccessLevelName}");

    public Task AddAccessLevelAsync(CreateAccessLevelRequest request, CancellationToken ct)
        => Record($"add-access-level:{request.Name}");

    public Task EditAccessLevelAsync(string currentName, CreateAccessLevelRequest request, CancellationToken ct)
        => Record($"edit-access-level:{currentName}->{request.Name}");

    // ---------- Účty ----------

    public Task<AccountDto?> GetAccountAsync(string accountId, CancellationToken ct)
        => Task.FromResult(Accounts.FirstOrDefault(a => a.Id == accountId));

    // ---------- Karty ----------

    public Task UpsertCardExAsync(string cardNumber, UpsertCardExRequest request, CancellationToken ct)
        => UpsertCardAsync(cardNumber, request.Card, ct);

    public Task<IReadOnlyList<CardDto>> GetCardsAsync(bool onlyWithoutHolder, CancellationToken ct)
    {
        var cards = _cardHolders.Values.SelectMany(h => h.Cards);
        if (onlyWithoutHolder)
            cards = cards.Where(c => c.CardHolderId is null);
        return Task.FromResult<IReadOnlyList<CardDto>>(cards.OrderBy(c => c.CardNumber).ToList());
    }

    public Task BulkAddCardsAsync(BulkAddCardsRequest request, CancellationToken ct)
        => Record($"bulk-add-cards:{request.StartNumber}-{request.StopNumber}");

    public Task BulkDeleteCardsAsync(BulkDeleteCardsRequest request, CancellationToken ct)
        => Record($"bulk-delete-cards:{request.StartNumber}-{request.StopNumber}");

    // ---------- Držitelé ----------

    public Task DeleteCardHolderAsync(string id, DeleteCardHolderOptions options, CancellationToken ct)
    {
        if (!_cardHolders.TryRemove(id, out _))
            throw new KeyNotFoundException($"Card holder '{id}' neexistuje.");

        return Record($"delete-cardholder:{id}:cards={options.DeleteCards}:images={options.DeleteImages}");
    }

    public Task<IReadOnlyList<CardHolderSearchFieldDto>> GetCardHolderSearchFieldsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CardHolderSearchFieldDto>>(
            [new("LastName", 0), new("FirstName", 1), new("Note", 2)]);

    public Task<IReadOnlyList<CardHolderDto>> SearchCardHoldersAsync(CardHolderSearchRequest request, CancellationToken ct)
    {
        var result = _cardHolders.Values.Where(holder => request.Criteria.All(criterion =>
            Field(holder, criterion.Field).Contains(criterion.Value, StringComparison.OrdinalIgnoreCase)));

        return Task.FromResult<IReadOnlyList<CardHolderDto>>(result.OrderBy(h => h.LastName).ToList());

        static string Field(CardHolderDto holder, string name) => name.ToLowerInvariant() switch
        {
            "firstname" => holder.FirstName,
            "lastname" => holder.LastName,
            "note" => holder.Note ?? "",
            _ => $"{holder.FirstName} {holder.LastName}",
        };
    }

    public Task<IReadOnlyList<NoteFieldTemplateDto>> GetNoteFieldTemplatesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<NoteFieldTemplateDto>>(
            [new("Oddělení", 0, "text"), new("Osobní číslo", 1, "text")]);

    public Task<CardHolderImageDto> GetCardHolderImageAsync(string id, int index, bool signature, CancellationToken ct)
    {
        var content = _cardHolderImages.GetValueOrDefault(ImageKey(id, index, signature));
        return Task.FromResult(new CardHolderImageDto(id, index,
            content is null ? 0 : Convert.FromBase64String(content).Length, content));
    }

    public Task ImportCardHolderImageAsync(string id, int index, bool signature, string contentBase64, CancellationToken ct)
    {
        _cardHolderImages[ImageKey(id, index, signature)] = contentBase64;
        return Task.CompletedTask;
    }

    public Task DeleteCardHolderImageAsync(string id, int index, bool signature, bool shortVariant, CancellationToken ct)
    {
        _cardHolderImages.TryRemove(ImageKey(id, index, signature), out _);
        return Task.CompletedTask;
    }

    private static string ImageKey(string id, int index, bool signature)
        => $"{id}/{(signature ? "sig" : "photo")}/{index}";

    // ---------- Časové zóny ----------

    public Task<IReadOnlyList<TimeZoneDto>> GetTimeZonesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TimeZoneDto>>(_timeZones.Values.OrderBy(z => z.Name).ToList());

    public Task<TimeZoneDto?> GetTimeZoneByNameAsync(string name, CancellationToken ct)
        => Task.FromResult(_timeZones.Values.FirstOrDefault(z =>
            z.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

    public Task<string> AddTimeZoneAsync(UpsertTimeZoneRequest request, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextTimeZoneId).ToString();
        _timeZones[id] = new TimeZoneDto(id, request.Name, request.Description, AccountName);
        return Task.FromResult(id);
    }

    public Task CreateTimeZoneAsync(UpsertTimeZoneRequest request, CancellationToken ct)
        => AddTimeZoneAsync(request, ct);

    public Task EditTimeZoneAsync(string currentName, UpsertTimeZoneRequest request, CancellationToken ct)
    {
        var existing = _timeZones.Values.FirstOrDefault(z => z.Name.Equals(currentName, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Časová zóna '{currentName}' neexistuje.");
        _timeZones[existing.Id] = existing with { Name = request.Name, Description = request.Description };
        return Task.CompletedTask;
    }

    public Task DeleteTimeZoneAsync(string timeZoneId, CancellationToken ct)
    {
        _timeZones.TryRemove(timeZoneId, out _);
        _timeZoneRanges.TryRemove(timeZoneId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TimeZoneRangeDto>> GetTimeZoneRangesAsync(string timeZoneId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TimeZoneRangeDto>>(
            _timeZoneRanges.GetValueOrDefault(timeZoneId) ?? []);

    public Task ConfigureTimeZoneRangesAsync(string timeZoneId, IReadOnlyList<TimeZoneRangeRequest> ranges, CancellationToken ct)
    {
        _timeZoneRanges[timeZoneId] = ranges
            .Select((r, i) => new TimeZoneRangeDto((i + 1).ToString(), timeZoneId, r.StartTime, r.EndTime, r.DayType))
            .ToList();
        return Task.CompletedTask;
    }

    public Task DeleteTimeZoneRangeAsync(string timeZoneId, string rangeId, CancellationToken ct)
    {
        if (_timeZoneRanges.TryGetValue(timeZoneId, out var ranges))
            _timeZoneRanges[timeZoneId] = ranges.Where(r => r.Id != rangeId).ToList();
        return Task.CompletedTask;
    }

    public Task<TimeZoneUsageDto> GetTimeZoneUsageAsync(string timeZoneId, CancellationToken ct)
        => Task.FromResult(new TimeZoneUsageDto(timeZoneId, [], [], [], [], [], []));

    public Task<IReadOnlyList<TimeZoneDto>> GetTimeZonesForReassignAsync(string timeZoneId, bool forOperators, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TimeZoneDto>>(
            _timeZones.Values.Where(z => z.Id != timeZoneId).ToList());

    public Task ReassignTimeZoneAsync(ReassignTimeZoneRequest request, CancellationToken ct)
        => Record($"reassign-tz:{request.CurrentTimeZoneId}->{request.NewTimeZoneId}");

    public Task DeletePanelTimeZoneAsync(string timeZoneId, IReadOnlyList<string> panelIds, CancellationToken ct)
        => Record($"delete-panel-tz:{timeZoneId}:{string.Join('|', panelIds)}");

    // ---------- Svátky ----------

    public Task<HolidayDto?> GetHolidayAsync(string holidayId, CancellationToken ct)
        => Task.FromResult(_holidays.GetValueOrDefault(holidayId));

    public Task<string> AddHolidayAsync(UpsertHolidayRequest request, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextHolidayId).ToString();
        _holidays[id] = new HolidayDto(id, request.Name, request.Year, request.Month, request.Day,
            request.Type, request.AppliesToAllYears);
        return Task.FromResult(id);
    }

    public Task EditHolidayAsync(string currentName, UpsertHolidayRequest request, CancellationToken ct)
    {
        var existing = _holidays.Values.FirstOrDefault(h => h.Name.Equals(currentName, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Svátek '{currentName}' neexistuje.");
        _holidays[existing.Id] = existing with
        {
            Name = request.Name,
            Year = request.Year,
            Month = request.Month,
            Day = request.Day,
            Type = request.Type,
            AppliesToAllYears = request.AppliesToAllYears,
        };
        return Task.CompletedTask;
    }

    public Task DeleteHolidayAsync(string holidayId, CancellationToken ct)
    {
        _holidays.TryRemove(holidayId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<HolidayGroupDto>> GetHolidayGroupsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<HolidayGroupDto>>(_holidayGroups.Values.OrderBy(g => g.Name).ToList());

    public Task<IReadOnlyList<HolidayDto>> GetHolidaysInGroupAsync(string holidayGroupId, CancellationToken ct)
    {
        var members = _holidayGroupMembers.GetValueOrDefault(holidayGroupId) ?? [];
        return Task.FromResult<IReadOnlyList<HolidayDto>>(
            members.Select(id => _holidays.GetValueOrDefault(id)).OfType<HolidayDto>().ToList());
    }

    public Task AddHolidayGroupAsync(UpsertHolidayGroupRequest request, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextHolidayId).ToString();
        _holidayGroups[id] = new HolidayGroupDto(id, request.Name, "1");
        _holidayGroupMembers[id] = [.. request.HolidayIds ?? []];
        return Task.CompletedTask;
    }

    public Task EditHolidayGroupAsync(string currentName, UpsertHolidayGroupRequest request, CancellationToken ct)
    {
        var existing = _holidayGroups.Values.FirstOrDefault(g => g.Name.Equals(currentName, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Skupina svátků '{currentName}' neexistuje.");
        _holidayGroups[existing.Id] = existing with { Name = request.Name };
        _holidayGroupMembers[existing.Id] = [.. request.HolidayIds ?? []];
        return Task.CompletedTask;
    }

    public Task DeleteHolidayGroupAsync(string holidayGroupId, CancellationToken ct)
    {
        _holidayGroups.TryRemove(holidayGroupId, out _);
        _holidayGroupMembers.TryRemove(holidayGroupId, out _);
        return Task.CompletedTask;
    }

    // ---------- Hardware ----------

    public Task<IReadOnlyList<HardwareDeviceDto>> GetHardwareDevicesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<HardwareDeviceDto>>(
            Readers.Select(r => new HardwareDeviceDto(r.Id, "1", r.Name, r.Description, "Reader")).ToList());

    public Task<IReadOnlyList<PanelDto>> GetPanelsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PanelDto>>(
        [
            new("1", "PRO4200-A1", "Budova A, 1. patro", "PRO4200"),
            new("2", "MPA2-B1", "Budova B", "MPA2"),
        ]);

    public Task<IReadOnlyList<PanelPointDto>> GetPanelOutputsAsync(long panelId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PanelPointDto>>(
            [new($"{panelId}01", "Relé 1", null), new($"{panelId}02", "Relé 2", null)]);

    public Task<IReadOnlyList<PanelPointDto>> GetPanelGroupsAsync(long panelId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PanelPointDto>>([new($"{panelId}10", "Skupina A", null)]);

    public Task<IReadOnlyList<TimeZoneDto>> GetPanelTimeZonesAsync(long panelId, bool configured, CancellationToken ct)
    {
        if (!configured)
            return Task.FromResult<IReadOnlyList<TimeZoneDto>>(_timeZones.Values.ToList());

        var ids = _panelTimeZones.GetValueOrDefault(panelId) ?? [];
        return Task.FromResult<IReadOnlyList<TimeZoneDto>>(
            ids.Select(id => _timeZones.GetValueOrDefault(id)).OfType<TimeZoneDto>().ToList());
    }

    public Task ConfigurePanelTimeZonesAsync(long panelId, IReadOnlyList<string> timeZoneIds, CancellationToken ct)
    {
        _panelTimeZones[panelId] = [.. timeZoneIds];
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<HolidayGroupDto>> GetPanelHolidayGroupsAsync(long panelId, CancellationToken ct)
    {
        var ids = _panelHolidayGroups.GetValueOrDefault(panelId) ?? [];
        return Task.FromResult<IReadOnlyList<HolidayGroupDto>>(
            ids.Select(id => _holidayGroups.GetValueOrDefault(id)).OfType<HolidayGroupDto>().ToList());
    }

    public Task ConfigurePanelHolidayGroupsAsync(long panelId, IReadOnlyList<string> holidayGroupIds, CancellationToken ct)
    {
        _panelHolidayGroups[panelId] = [.. holidayGroupIds];
        return Task.CompletedTask;
    }

    public Task ConfigureOutputTimeZoneAsync(long panelId, long outputId, string timeZoneId, int? lockUnlock, CancellationToken ct)
        => Record($"configure-output-tz:{panelId}/{outputId}:{timeZoneId}:{lockUnlock}");

    public Task ConfigureGroupTimeZoneAsync(long panelId, long groupId, string timeZoneId, CancellationToken ct)
        => Record($"configure-group-tz:{panelId}/{groupId}:{timeZoneId}");

    public Task<IReadOnlyList<AccessAreaBranchDto>> GetAccessAreaBranchesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AccessAreaBranchDto>>([new("1", "Budova A"), new("2", "Budova B")]);

    public Task<IReadOnlyList<ReaderDto>> GetReadersInBranchAsync(string branchName, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ReaderDto>>(
            Readers.Where(r => r.Description?.Contains(branchName, StringComparison.OrdinalIgnoreCase) == true).ToList());

    public Task<IReadOnlyList<TimeZoneDto>> GetReaderTimeZonesAsync(string readerName, bool forAccount, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TimeZoneDto>>(_timeZones.Values.ToList());

    public Task<IReadOnlyList<TimeZoneDto>> GetBranchTimeZonesAsync(string branchName, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TimeZoneDto>>(_timeZones.Values.ToList());

    public Task<string?> GetAssociatedGroupAsync(string accessLevelName, string readerName, CancellationToken ct)
        => Task.FromResult<string?>("110");

    public Task<IReadOnlyList<PanelPointDto>> GetReaderGroupsAsync(string readerName, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PanelPointDto>>([new("110", "Skupina A", null)]);

    // ---------- Systém ----------

    public Task<SystemInfoDto> GetSystemInfoAsync(CancellationToken ct)
        => Task.FromResult(new SystemInfoDto(
            DataSourceName: "WINPAK-MOCK",
            ServerTimeZone: "Central Europe Standard Time",
            DaylightSavingEnabled: true,
            ServerTimeZoneOffsetMinutes: 60,
            MaxCardNumberLength: 10,
            CardNumbersAreNumeric: true,
            AccessLevelType: 1,
            CurrentOperator: new OperatorDto(1, "mock-operator"),
            Domains: ["FNMH"]));

    public Task<ScheduleDto?> GetScheduleAsync(string scheduleId, CancellationToken ct)
        => Task.FromResult<ScheduleDto?>(new ScheduleDto(scheduleId, "Denní report", "1", 1, 1, 1, true, false, false));

    public Task UpsertScheduleAsync(ScheduleDto schedule, CancellationToken ct)
        => Record($"upsert-schedule:{schedule.Id}:{schedule.Name}");

    public Task DeleteScheduleAsync(string scheduleId, CancellationToken ct)
        => Record($"delete-schedule:{scheduleId}");

    public Task<TemplateDto?> GetTemplateAsync(string templateId, CancellationToken ct)
        => Task.FromResult<TemplateDto?>(new TemplateDto(templateId, "Přehled průchodů", "1", 1, "<template/>"));

    public Task UpsertTemplateAsync(TemplateDto template, CancellationToken ct)
        => Record($"upsert-template:{template.Id}:{template.Name}");

    public Task DeleteTemplateAsync(string templateId, CancellationToken ct)
        => Record($"delete-template:{templateId}");

    public Task<BadgeDto> GetBadgeAsync(string badgeId, CancellationToken ct)
        => Task.FromResult(new BadgeDto(badgeId, "<badge/>", 54, 86));

    public Task<LookupResultDto> LookupAsync(LookupKind kind, string value, CancellationToken ct)
        => Task.FromResult(new LookupResultDto(kind, value, kind switch
        {
            LookupKind.DeviceName => Readers.FirstOrDefault(r => r.Id == value)?.Name,
            LookupKind.AccountByDevice or LookupKind.AccountName => "FNMH",
            LookupKind.AccessLevelName => AccessLevels.FirstOrDefault(al => al.Id == value)?.Name,
            LookupKind.TimeZoneName => _timeZones.GetValueOrDefault(value)?.Name,
            LookupKind.SubAccountName => "Default",
            LookupKind.AccountEmails => "acs@fnmh.cz",
            LookupKind.ReaderTimeZoneDetails => "<Readers/>",
            LookupKind.LoopTimeZones => "<Loops/>",
            LookupKind.ReaderDirectPoint => "1/1",
            LookupKind.PanelGroupCheck => bool.TrueString,
            _ => null,
        }));

    public Task<TimeZoneDto?> GetAssociatedTimeZoneAsync(AssociatedTimeZoneQuery query, CancellationToken ct)
        => Task.FromResult(_timeZones.Values.FirstOrDefault());

    public Task<IReadOnlyList<PanelDto>> GetPanelsUsingHolidayGroupAsync(string holidayGroupId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PanelDto>>([]);

    public Task DeleteAccessLevelWithReplacementAsync(string accessLevelId, string replacementId, bool multiple, CancellationToken ct)
        => Record($"delete-access-level-with-replacement:{accessLevelId}->{replacementId}");

    public Task WriteCardObjectAsync(string cardNumber, UpsertCardRequest request, bool edit, CancellationToken ct)
        => UpsertCardAsync(cardNumber, request, ct);

    // ---------- Komunikační server ----------

    public Task AcknowledgeAlarmAsync(long hid, int point, CancellationToken ct)
        => Record($"ack-alarm:{hid}/{point}");

    public Task ClearAlarmAsync(long hid, int point, CancellationToken ct)
        => Record($"clear-alarm:{hid}/{point}");

    public Task AddNoteAsync(long hid, int point, string note, CancellationToken ct)
        => Record($"note:{hid}/{point}:{note}");

    public Task<TransactionDetailDto> GetTransactionDetailsAsync(long hid, int point, CancellationToken ct)
        => Task.FromResult(new TransactionDetailDto(hid.ToString(), point, $"Mock transakce {hid}/{point}"));

    public Task ShuntAlarmAsync(long hid, bool shunt, CancellationToken ct)
        => Record($"{(shunt ? "shunt" : "unshunt")}:{hid}");

    public Task LockEntryPointAsync(long hid, int point, bool unlock, CancellationToken ct)
        => Record($"{(unlock ? "unlock" : "lock")}-entry-point:{hid}/{point}");

    public Task UnshuntAlarmPointAsync(long hid, int point, CancellationToken ct)
        => Record($"unshunt-point:{hid}/{point}");

    public Task<int> GetDoorStatusCodeAsync(long hid, CancellationToken ct)
        => Task.FromResult(0);

    public Task BufferAsync(long hid, int mode, bool buffer, CancellationToken ct)
        => Record($"{(buffer ? "buffer" : "unbuffer")}:{hid}:{mode}");

    public Task EnergizeAsync(long hid, bool energize, CancellationToken ct)
        => Record($"{(energize ? "energize" : "de-energize")}:{hid}");

    public Task RestoreTimeZoneAsync(long hid, CancellationToken ct)
        => Record($"restore-tz:{hid}");

    public Task InitializePanelAsync(long hid, PanelInitializeRequest request, CancellationToken ct)
        => Record($"panel-init:{hid}:{string.Join('|', request.Tasks)}");

    public Task CancelPanelInitializeAsync(long hid, CancellationToken ct)
        => Record($"panel-init-cancel:{hid}");

    public Task RefreshPanelTimeZonesAsync(long hid, CancellationToken ct)
        => Record($"panel-refresh-tz:{hid}");

    public Task LockUnlockAllDoorsAsync(long accountId, bool shouldLock, CancellationToken ct)
        => Record($"lock-all-doors:{accountId}:{shouldLock}");

    public Task<int> RefreshDoorsAsync(long accountId, CancellationToken ct)
        => Record($"refresh-doors:{accountId}", 0);

    public Task ExecuteDoorScheduleAsync(DoorScheduleRequest request, CancellationToken ct)
        => Record($"door-schedule:{request.PanelHid}/{request.EntranceId}");

    public Task<NetAxsDoorModeDto> GetNetAxsDoorModeAsync(long hid, CancellationToken ct)
        => Task.FromResult(_netAxsModes.GetValueOrDefault(hid,
            new NetAxsDoorModeDto(0, 0, 1, 0, 0, 0, 1, 0, 0, 0)));

    public Task SetNetAxsDoorModeAsync(long hid, NetAxsDoorModeDto mode, CancellationToken ct)
    {
        _netAxsModes[hid] = mode;
        return Task.CompletedTask;
    }

    public Task<int> GetDeviceStatusAsync(long hid, int deviceType, CancellationToken ct)
        => Task.FromResult(1);

    public Task<int> GetDefaultReaderModeAsync(long hid, CancellationToken ct)
        => Task.FromResult((int)DoorMode.CardOnly);

    public Task<IReadOnlyList<string>> GetEventFiltersAsync(bool commServer, CancellationToken ct)
    {
        var filters = commServer ? _commServerFilters : _eventFilters;
        lock (filters)
            return Task.FromResult<IReadOnlyList<string>>([.. filters]);
    }

    public Task AddEventFilterAsync(long id, bool commServer, CancellationToken ct)
    {
        var filters = commServer ? _commServerFilters : _eventFilters;
        lock (filters)
        {
            if (!filters.Contains(id.ToString()))
                filters.Add(id.ToString());
        }

        return Task.CompletedTask;
    }

    public Task RemoveEventFilterAsync(long id, bool commServer, CancellationToken ct)
    {
        var filters = commServer ? _commServerFilters : _eventFilters;
        lock (filters)
            filters.Remove(id.ToString());
        return Task.CompletedTask;
    }

    public Task<MusterElementDto> GetMusterAsync(long areaId, long accountId, int sortField, int sortOrder, CancellationToken ct)
        => Task.FromResult(new MusterElementDto($"<Muster area=\"{areaId}\" account=\"{accountId}\" />"));

    public Task ExecuteCustomCommandAsync(long hid, string command, CancellationToken ct)
        => Record($"custom-command:{hid}:{command}");
}
