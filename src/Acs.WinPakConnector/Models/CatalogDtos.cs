namespace Acs.WinPakConnector.Models;

// ---------- Časové zóny ----------

/// <summary>Časová zóna WIN-PAK (objekt <c>NCIHelper.TimeZone</c>).</summary>
public record TimeZoneDto(string Id, string Name, string? Description, string? AccountName);

/// <summary>Interval v rámci časové zóny (objekt <c>NCIHelper.TimeZoneRange</c>).</summary>
public record TimeZoneRangeDto(string Id, string TimeZoneId, string? StartTime, string? EndTime, int DayType);

public record UpsertTimeZoneRequest(string Name, string? Description = null, IReadOnlyList<string>? AccountIds = null);

public record TimeZoneRangeRequest(int DayType, string StartTime, string EndTime);

/// <summary>Co všechno časovou zónu používá — WIN-PAK ji nedovolí smazat, dokud je někde navázaná.</summary>
public record TimeZoneUsageDto(
    string TimeZoneId,
    IReadOnlyList<string> Operators,
    IReadOnlyList<string> Panels,
    IReadOnlyList<string> AccessLevels,
    IReadOnlyList<string> ActionGroups,
    IReadOnlyList<string> Cards,
    IReadOnlyList<string> Devices);

/// <summary>Přeřazení entit ze staré časové zóny na novou; posílají se jen vyplněné skupiny.</summary>
public record ReassignTimeZoneRequest(
    string CurrentTimeZoneId,
    string NewTimeZoneId,
    IReadOnlyList<string>? OperatorIds = null,
    IReadOnlyList<string>? AccessLevelIds = null,
    IReadOnlyList<string>? ActionGroupIds = null,
    IReadOnlyList<string>? CardIds = null,
    IReadOnlyList<string>? DeviceIds = null);

// ---------- Svátky ----------

/// <summary>Svátek. <c>Type</c> odpovídá číselníku <c>HolidayType</c> ve WIN-PAK.</summary>
public record HolidayDto(string Id, string Name, int Year, int Month, int Day, int Type, bool AppliesToAllYears);

public record HolidayGroupDto(string Id, string Name, string? AccountId);

public record UpsertHolidayRequest(string Name, int Year, int Month, int Day, int Type = 0, bool AppliesToAllYears = false);

public record UpsertHolidayGroupRequest(string Name, IReadOnlyList<string>? HolidayIds = null,
    IReadOnlyList<string>? MasterHolidayIds = null);

// ---------- Hardware ----------

/// <summary>Panel (řídicí jednotka) v účtu.</summary>
public record PanelDto(string Id, string Name, string? Description, string? DeviceType);

/// <summary>Výstup nebo skupina výstupů panelu.</summary>
public record PanelPointDto(string Id, string Name, string? Description);

/// <summary>Větev přístupové oblasti (access area).</summary>
public record AccessAreaBranchDto(string Id, string Name);

/// <summary>Obecné hardwarové zařízení (ADV) z databázového API.</summary>
public record HardwareDeviceDto(string Hid, string DeviceId, string Name, string? Description, string? DeviceType);

// ---------- Přístupové úrovně ----------

public record CreateAccessLevelRequest(string Name, string? Description = null, IReadOnlyList<string>? AccountIds = null);

/// <summary>Nastavení čteček a časové zóny pro přístupovou úroveň (<c>ConfigureAccessLevel</c>).</summary>
public record ConfigureAccessLevelRequest(IReadOnlyList<string> ReaderNames, string TimeZoneName);

/// <summary>Nastavení jedné čtečky v přístupové úrovni (<c>ConfigureEntranceAccess</c>).</summary>
public record ConfigureEntranceRequest(string ReaderName, string TimeZoneName, string? GroupName = null);

/// <summary>Úplná definice přístupové úrovně pro <c>AddUpdateAL</c>.</summary>
public record UpsertAccessLevelRequest(
    string Name,
    string? Description = null,
    IReadOnlyList<string>? SubAccountIds = null,
    IReadOnlyList<string>? ReaderIds = null,
    IReadOnlyList<string>? ReaderTimeZoneIds = null,
    IReadOnlyList<string>? ReaderGroupIds = null);

/// <summary>Náhrada úrovně na kartách při jejím rušení (<c>ReassignAccessLevel</c>).</summary>
public record ReassignAccessLevelRequest(string NewAccessLevelName);

// ---------- Karty ----------

/// <summary>Hromadné založení rozsahu karet (<c>BulkAddCards</c>).</summary>
public record BulkAddCardsRequest(
    string StartNumber,
    string StopNumber,
    CardStatus Status = CardStatus.Active,
    DateTime? ActivationDate = null,
    DateTime? ExpirationDate = null,
    IReadOnlyList<string>? AccessLevelIds = null);

public record BulkDeleteCardsRequest(string StartNumber, string StopNumber);

/// <summary>Doplňková nastavení karty pro NetAXS panely (<c>AddUpdateCardEx</c>).</summary>
public record NetAxsCardOptions(
    bool TemporaryCard = false,
    int CardType = 0,
    int UsageLimit = 0,
    bool LimitedCard = false,
    long Trigger = 0);

/// <summary>Zápis karty rozšířený o NetAXS volby.</summary>
public record UpsertCardExRequest(UpsertCardRequest Card, NetAxsCardOptions NetAxs);

// ---------- Držitelé ----------

/// <summary>Pole, podle kterých umí WIN-PAK vyhledávat držitele.</summary>
public record CardHolderSearchFieldDto(string Name, int Index);

/// <summary>Vyhledání držitelů přes <c>GetCardHoldersOnSearch</c>; typ porovnání dle číselníku <c>ComparisonType</c>.</summary>
public record CardHolderSearchRequest(IReadOnlyList<CardHolderSearchCriterion> Criteria);

public record CardHolderSearchCriterion(string Field, string Value, int ComparisonType = 0);

public record DeleteCardHolderOptions(bool DeleteCards = true, bool DeleteImages = true);

/// <summary>Šablona poznámkových polí účtu.</summary>
public record NoteFieldTemplateDto(string Name, int Index, string? Definition);

/// <summary>Obrázek držitele (foto nebo podpis) v base64.</summary>
public record CardHolderImageDto(string CardHolderId, int Index, long Size, string? ContentBase64);

public record ImportImageRequest(string ContentBase64);

// ---------- Systém ----------

public record OperatorDto(int Id, string Name);

/// <summary>Systémové údaje WIN-PAK serveru pro diagnostiku.</summary>
public record SystemInfoDto(
    string? DataSourceName,
    string? ServerTimeZone,
    bool DaylightSavingEnabled,
    int ServerTimeZoneOffsetMinutes,
    int MaxCardNumberLength,
    bool CardNumbersAreNumeric,
    int AccessLevelType,
    OperatorDto? CurrentOperator,
    IReadOnlyList<string> Domains);

/// <summary>Plán reportu (objekt <c>NCIHelper.Schedule</c>).</summary>
public record ScheduleDto(
    string Id,
    string Name,
    string? AccountId,
    int ScheduleType,
    int Frequency,
    int ReportType,
    bool Print,
    bool Email,
    bool Fax);

/// <summary>Šablona reportu (objekt <c>NCIHelper.Template</c>).</summary>
public record TemplateDto(string Id, string Name, string? AccountId, int Type, string? Definition);

public record BadgeDto(string Id, string? Data, int Height, int Width);

/// <summary>Drobné dotazy WIN-PAKu typu „přelož id na název“ nebo diagnostický souhrn.</summary>
public enum LookupKind
{
    /// <summary>Název zařízení podle jeho HID.</summary>
    DeviceName,
    /// <summary>Účet, kterému zařízení patří.</summary>
    AccountByDevice,
    /// <summary>Název přístupové úrovně podle id.</summary>
    AccessLevelName,
    /// <summary>Název časové zóny podle id.</summary>
    TimeZoneName,
    /// <summary>Název účtu podle id.</summary>
    AccountName,
    /// <summary>Název podúčtu podle id.</summary>
    SubAccountName,
    /// <summary>E-mailové adresy účtu pro reporty.</summary>
    AccountEmails,
    /// <summary>Souhrn časových zón čteček v účtu.</summary>
    ReaderTimeZoneDetails,
    /// <summary>Souhrn časových zón smyčky účtu.</summary>
    LoopTimeZones,
    /// <summary>Přímý bod a časová zóna čtečky (vrací dvě hodnoty oddělené lomítkem).</summary>
    ReaderDirectPoint,
    /// <summary>Zda má panel zapnutou volbu skupin.</summary>
    PanelGroupCheck,
}

public record LookupResultDto(LookupKind Kind, string Value, string? Result);

/// <summary>Na co se ptáme při zjišťování navázané časové zóny.</summary>
public record AssociatedTimeZoneQuery(
    string? AccessLevelName = null,
    string? ReaderName = null,
    long? PanelId = null,
    long? OutputId = null,
    long? GroupId = null,
    int? LockUnlock = null);

// ---------- Komunikační server ----------

/// <summary>Detail transakce alarmu nebo události (<c>GetDetailsByID</c>).</summary>
public record TransactionDetailDto(string Hid, int Point, string? Details);

/// <summary>Režim dveří NetAXS panelu (<c>GetNetAXSDoorModeByHID</c>).</summary>
public record NetAxsDoorModeDto(
    int DisableDoorTimeZone,
    int LockdownReaderTimeZone,
    int CardOnlyTimeZone,
    int PinOnlyTimeZone,
    int CardOrPinTimeZone,
    int CardAndPinTimeZone,
    int CardOnlyPriority,
    int PinOnlyPriority,
    int CardOrPinPriority,
    int CardAndPinPriority);

/// <summary>Prvek muster reportu (kdo je v jaké oblasti).</summary>
public record MusterElementDto(string? Raw);

public record AlarmPointRequest(int Point = 0);

public record AlarmNoteRequest(string Note, int Point = 0);

public record BufferRequest(int Mode = 0);

public record CustomCommandRequest(string Command);

public record DoorScheduleRequest(long PanelHid, int PanelType, long EntranceId, long EntrancePointId, long TimeZoneId);

public record PanelInitializeRequest(int PanelType, IReadOnlyList<int> Tasks);

public record LockAllDoorsRequest(bool Lock);
