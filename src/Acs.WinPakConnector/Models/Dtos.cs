namespace Acs.WinPakConnector.Models;

/// <summary>Stav karty podle WIN-PAK (hodnoty <c>lCardStatus</c>).</summary>
public enum CardStatus
{
    Unknown = 0,
    Active = 1,
    Inactive = 2,
    Trace = 3,
    LostOrStolen = 4,
}

/// <summary>Režim dveří podle WIN-PAK (parametr <c>lVal</c> metody <c>DoorModeByHID</c>).</summary>
public enum DoorMode
{
    Disabled = 1,
    Unlocked = 2,
    Locked = 3,
    SiteCodeOnly = 4,
    CardOnly = 5,
    PinOnly = 6,
    CardAndPin = 7,
    CardOrPin = 8,
}

/// <summary>Účet WIN-PAK — karty i držitelé jsou mezi účty oddělené.</summary>
public record AccountDto(string Id, string Name, IReadOnlyList<SubAccountDto> SubAccounts);

public record SubAccountDto(string Id, string Name);

/// <summary>Čtečka (hardwarové zařízení) tak, jak ji eviduje WIN-PAK.</summary>
public record ReaderDto(
    string Id,
    string Name,
    string? Description,
    string? PanelName,
    string? AccountName,
    bool IsActive);

/// <summary>Přístupová úroveň (access level) ve WIN-PAK.</summary>
public record AccessLevelDto(
    string Id,
    string Name,
    string? Description);

/// <summary>
/// Karta. Pozor: nositelem oprávnění je ve WIN-PAKu karta, nikoli držitel —
/// přístupové úrovně jsou proto zde, ne na <see cref="CardHolderDto"/>.
/// </summary>
public record CardDto(
    string CardNumber,
    string? RecordId,
    string? CardHolderId,
    CardStatus Status,
    int Issue,
    DateTime? ActivationDate,
    DateTime? ExpirationDate,
    IReadOnlyList<string> AccessLevelIds);

/// <summary>Držitel karty. <see cref="AccessLevelIds"/> je sjednocení úrovní jeho karet.</summary>
public record CardHolderDto(
    string Id,
    string FirstName,
    string LastName,
    string? Note,
    IReadOnlyList<CardDto> Cards,
    IReadOnlyList<string> AccessLevelIds,
    /// <summary>E-mail držitele (<c>EmailID</c> ve WIN-PAKu).</summary>
    string? Email = null,
    /// <summary>Externí reference držitele (<c>ExtRefID</c>) — místo pro osobní číslo z personálního systému.</summary>
    string? ExternalRef = null);

/// <summary>Zařízení připojené ke komunikačnímu serveru (ADV).</summary>
public record DeviceDto(string Hid, string Name, string? DeviceType);

/// <summary>Stav dveří z <c>GetDoorStatus2</c>; null = WIN-PAK stav nezná (-1).</summary>
public record DoorStatusDto(
    string Hid,
    string? DeviceName,
    bool? IsOpen,
    bool? IsShunted,
    bool? ForcedOpen,
    bool? Ajar,
    string? Account,
    string? SubAccount);

/// <summary>Stav jednoho serveru WIN-PAK z <c>IsConnected2</c>.</summary>
public record ServerStatusDto(string ServerId, string ServerName, bool Connected, string? ServerType);

/// <summary>Souhrnný stav spojení konektoru s WIN-PAK.</summary>
public record ConnectorStatusDto(
    bool DatabaseServerConnected,
    IReadOnlyList<ServerStatusDto> Servers,
    string? Error,
    /// <summary>WIN-PAK právě zpracovává jiné volání (název a doba); stav se za ně neřadí.</summary>
    string? Busy = null);

/// <summary>Informace o běžícím konektoru.</summary>
public record ConnectorInfoDto(
    string Version,
    string ProviderMode,
    bool SupportsWrite,
    bool SupportsDoorControl,
    string? AccountName);

// ---------- Požadavky ----------

public record AssignAccessLevelRequest(string AccessLevelId);

/// <summary>Založení nebo úprava karty (mapuje se na <c>AddUpdateCard</c>).</summary>
public record UpsertCardRequest(
    string? CardHolderId,
    CardStatus Status = CardStatus.Active,
    int Issue = 0,
    DateTime? ActivationDate = null,
    DateTime? ExpirationDate = null,
    string? Pin = null,
    IReadOnlyList<string>? AccessLevelIds = null);

public record UpsertCardHolderRequest(string FirstName, string LastName, string? Note = null);

/// <summary>Krátké otevření dveří; bez délky se použije <c>PulseByHID</c>.</summary>
public record PulseDoorRequest(int? Seconds = null);

public record DoorModeRequest(DoorMode Mode);
