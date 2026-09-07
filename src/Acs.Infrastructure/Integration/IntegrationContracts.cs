using System.Text.Json.Serialization;
using Acs.Domain.Entities;

namespace Acs.Infrastructure.Integration;

/// <summary>
/// Datové tvary integračního API ACS (<c>docs/integrace/acs-integration-api.yaml</c>).
/// Názvy polí jsou anglicky podle kontraktu, hodnoty normalizované stejně jako v ACS.
/// </summary>
public static class IntegrationContract
{
    public const string Version = "1.0";

    /// <summary>Identifikátor integrace parkovacího systému v událostech a nastavení.</summary>
    public const string ParkingSource = "parking";

    /// <summary>Skupina „všechny areály“ v oprávněních (<c>groupId</c>).</summary>
    public const string AllSitesGroupId = "site:*";

    public static string CredentialTypeName(IdentifierType type) => type switch
    {
        IdentifierType.Card => "card",
        IdentifierType.LicensePlate => "licensePlate",
        IdentifierType.Pin => "pin",
        IdentifierType.Tag => "tag",
        IdentifierType.Biometric => "biometric",
        _ => "other",
    };

    public static IdentifierType? ParseCredentialType(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "card" or "karta" => IdentifierType.Card,
        "licenseplate" or "licence_plate" or "license_plate" or "plate" or "spz" or "rz" => IdentifierType.LicensePlate,
        "pin" => IdentifierType.Pin,
        "tag" or "chip" or "čip" => IdentifierType.Tag,
        "biometric" => IdentifierType.Biometric,
        "other" => IdentifierType.Other,
        _ => null,
    };
}

public record CredentialRef(string Type, string Value);

/// <summary><c>POST /authorization-checks</c> — co systém načetl a kde.</summary>
public record AuthorizationRequest(
    string AccessPointId,
    CredentialRef Credential,
    string? Direction = "in",
    DateTime? OccurredAt = null,
    double? Confidence = null);

/// <summary>Důvody rozhodnutí podle kontraktu (řetězce jdou ven beze změny).</summary>
public static class DecisionReason
{
    public const string Allowed = "allowed";
    public const string CredentialUnknown = "credentialUnknown";
    public const string CredentialExpired = "credentialExpired";
    public const string PersonEnded = "personEnded";
    public const string NoEntitlement = "noEntitlement";
    public const string EntitlementExpired = "entitlementExpired";
    public const string AccessPointUnknown = "accessPointUnknown";
}

/// <summary><c>AuthorizationDecision</c> — rozhodnutí s důvodem a údaji pro obsluhu.</summary>
public record AuthorizationDecision(
    string Decision,
    string Reason,
    string? PersonId,
    string? PersonName,
    string? EntitlementId,
    DateTime? ValidTo,
    int CacheTtlSeconds,
    DateTime DecidedAt,
    string TraceId)
{
    [JsonIgnore] public bool IsAllowed => Decision == "allow";
}

/// <summary>Událost hlášená systémem (<c>POST /events</c>).</summary>
public record IntegrationEventInput(
    string Id,
    string Type,
    DateTime OccurredAt,
    string? AccessPointId = null,
    string? PersonId = null,
    CredentialRef? Credential = null,
    string? Direction = null,
    string? DecisionReason = null,
    double? Confidence = null,
    Dictionary<string, string>? Details = null);

public record IntegrationEventResult(string Id, string Status, string? Problem = null);

public record IntegrationEventBatchResult(int Accepted, int Rejected, int Duplicates, List<IntegrationEventResult> Results);

/// <summary><c>Person</c> v integračním API — jen to, co parkovací systém potřebuje.</summary>
public record PersonDto(
    string Id,
    string? PersonalNumber,
    string FirstName,
    string LastName,
    string? Department,
    string Status,
    Dictionary<string, string> ExternalIds,
    DateTime? UpdatedAt);

public record CredentialDto(
    string Type,
    string Value,
    bool IsActive,
    DateTime? ValidFrom,
    DateTime? ValidTo,
    string? Note);

public record EntitlementTarget(string? AccessPointId, string? GroupId);

public record EntitlementSource(string? RequestId, DateTime? ApprovedAt, string Reason);

/// <summary>Parkovací povolení promítnuté na oprávnění kontraktu.</summary>
public record EntitlementDto(
    string Id,
    string PersonId,
    EntitlementTarget Target,
    string Status,
    DateTime? ValidFrom,
    DateTime? ValidTo,
    EntitlementSource Source,
    DateTime UpdatedAt,
    List<CredentialDto> Credentials,
    string? PermitNumber,
    string? PermitType,
    string? ParkingSpot);

public record AccessPointDto(
    string Id,
    string Kind,
    string Name,
    string? Location,
    Dictionary<string, string> ExternalIds,
    bool IsActive);

public record PagedResult<T>(List<T> Items, string? NextCursor);
