namespace Acs.WinPakConnector.Models;

/// <summary>Čtečka (reader) tak, jak ji eviduje WIN-PAK.</summary>
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

/// <summary>Karta přiřazená držiteli.</summary>
public record CardDto(
    string CardNumber,
    string? Status,
    DateTime? ActivationDate,
    DateTime? ExpirationDate);

/// <summary>Držitel karty (card holder) ve WIN-PAK.</summary>
public record CardHolderDto(
    string Id,
    string FirstName,
    string LastName,
    string? Note,
    IReadOnlyList<CardDto> Cards,
    IReadOnlyList<string> AccessLevelIds);

/// <summary>Požadavek na přiřazení přístupové úrovně držiteli karty.</summary>
public record AssignAccessLevelRequest(string AccessLevelId);

/// <summary>Informace o běžícím konektoru.</summary>
public record ConnectorInfoDto(
    string Version,
    string ProviderMode,
    bool SupportsWrite);
