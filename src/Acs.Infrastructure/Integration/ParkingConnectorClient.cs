using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Acs.Infrastructure.Settings;

namespace Acs.Infrastructure.Integration;

/// <summary>Přiznané možnosti konektoru (<c>GET /capabilities</c> v <c>connector-api.yaml</c>).</summary>
public record ConnectorCapabilities(
    ConnectorInfo Connector,
    string ContractVersion,
    List<string> Operations,
    List<string>? CredentialTypes,
    List<string>? EntitlementTargets,
    bool? SupportsValidity);

public record ConnectorInfo(string Name, string Version, string? TargetSystem);

public record ConnectorPersonUpsert(
    string? PersonalNumber, string FirstName, string LastName, string? Department, string Status,
    DateTime? ValidFrom = null, DateTime? ValidTo = null);

public record ConnectorUpsertResult(string TargetId, bool? Created);

public record ConnectorCredential(string Type, string Value, DateTime? ValidFrom, DateTime? ValidTo);

public record ConnectorEntitlement(string? AccessPointId, string? GroupId, DateTime? ValidFrom, DateTime? ValidTo,
    string? EntitlementId);

public record ConnectorEntitlementResult(int Applied, int? Removed, List<string>? TargetReferences,
    List<string>? UnknownTargets);

/// <summary>Chyba konektoru s HTTP stavem — text jde do fronty správce parkování.</summary>
public class ParkingConnectorException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// HTTP klient ke konektoru parkovacího systému (vzor A — ACS zapisuje). Konektor
/// implementuje <c>docs/integrace/connector-api.yaml</c> a překládá volání na API
/// konkrétního systému (GreenCenter GPSW Kernel apod.). Adresa a klíč jsou v Nastavení →
/// Parkovací systém; bez adresy je klient „nenakonfigurovaný“ a nic nevolá.
/// </summary>
public class ParkingConnectorClient(HttpClient httpClient, SettingsService settings)
{
    public const string HttpClientName = "ParkingConnector";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
        => !string.IsNullOrWhiteSpace(await settings.GetAsync(SettingKeys.ParkingSystemConnectorBaseUrl, ct));

    public async Task<ConnectorCapabilities?> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Get, "capabilities", ct);
        using var response = await httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<ConnectorCapabilities>(Json, ct);
    }

    public async Task<ConnectorUpsertResult?> UpsertPersonAsync(string personId, ConnectorPersonUpsert person,
        CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Put, $"persons/{Uri.EscapeDataString(personId)}", ct);
        request.Content = JsonContent.Create(person, options: Json);
        using var response = await httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return response.StatusCode == HttpStatusCode.NoContent
            ? null
            : await response.Content.ReadFromJsonAsync<ConnectorUpsertResult>(Json, ct);
    }

    public async Task SetCredentialsAsync(string personId, IReadOnlyList<ConnectorCredential> credentials,
        CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Put, $"persons/{Uri.EscapeDataString(personId)}/credentials", ct);
        request.Content = JsonContent.Create(new { credentials }, options: Json);
        using var response = await httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task<ConnectorEntitlementResult?> SetEntitlementsAsync(string personId,
        IReadOnlyList<ConnectorEntitlement> entitlements, CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Put, $"persons/{Uri.EscapeDataString(personId)}/entitlements", ct);
        request.Content = JsonContent.Create(new { entitlements }, options: Json);
        using var response = await httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return response.StatusCode == HttpStatusCode.NoContent
            ? null
            : await response.Content.ReadFromJsonAsync<ConnectorEntitlementResult>(Json, ct);
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(HttpMethod method, string path, CancellationToken ct)
    {
        var baseUrl = await settings.GetAsync(SettingKeys.ParkingSystemConnectorBaseUrl, ct);
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("Konektor parkovacího systému není nakonfigurován (Nastavení → Parkovací systém).");

        var request = new HttpRequestMessage(method, new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path));
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (await settings.GetAsync(SettingKeys.ParkingSystemConnectorApiKey, ct) is { Length: > 0 } apiKey)
            request.Headers.Add("X-Api-Key", apiKey);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        var detail = TryReadProblemDetail(body) ?? (body.Length > 300 ? body[..300] : body);
        var prefix = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "konektor odmítl API klíč",
            HttpStatusCode.NotImplemented => "konektor operaci nepodporuje",
            HttpStatusCode.BadGateway => "parkovací systém je nedostupný",
            HttpStatusCode.UnprocessableEntity => "konektor neumí přeložit cíl oprávnění",
            _ => $"konektor vrátil {(int)response.StatusCode}",
        };
        throw new ParkingConnectorException(response.StatusCode,
            string.IsNullOrWhiteSpace(detail) ? prefix : $"{prefix}: {detail}");
    }

    private static string? TryReadProblemDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.TrimStart()[0] != '{')
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var detail = root.TryGetProperty("detail", out var d) ? d.GetString() : null;
            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            var unknown = root.TryGetProperty("unknownTargets", out var u) && u.ValueKind == JsonValueKind.Array
                ? string.Join(", ", u.EnumerateArray().Select(x => x.ToString()))
                : null;
            var text = detail ?? title;
            return unknown is { Length: > 0 } ? $"{text} (neznámé cíle: {unknown})" : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
