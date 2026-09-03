using System.Net.Http.Json;
using Acs.Infrastructure.Settings;

namespace Acs.Infrastructure.WinPak;

public record WinPakReader(string Id, string Name, string? Description, string? PanelName, string? AccountName, bool IsActive);
public record WinPakAccessLevel(string Id, string Name, string? Description);

public record WinPakTimeZone(string Id, string Name, string? Description, string? AccountName);

/// <summary>
/// Úplná definice přístupové úrovně pro zápis (<c>AddUpdateAL</c> ve WIN-PAKu):
/// čtečky a k nim paralelně časové zóny, ve kterých na nich úroveň platí.
/// </summary>
public record WinPakAccessLevelDefinition(
    string Name,
    string? Description,
    IReadOnlyList<string> ReaderIds,
    IReadOnlyList<string> ReaderTimeZoneIds,
    IReadOnlyList<string>? SubAccountIds = null,
    IReadOnlyList<string>? ReaderGroupIds = null);

public record WinPakInfo(string Version, string ProviderMode, bool SupportsWrite,
    bool SupportsDoorControl = false, string? AccountName = null);

/// <summary>Stav karty podle WIN-PAK.</summary>
public enum WinPakCardStatus { Unknown = 0, Active = 1, Inactive = 2, Trace = 3, LostOrStolen = 4 }

/// <summary>Karta ve WIN-PAK. Přístupové úrovně patří kartě, ne držiteli.</summary>
public record WinPakCard(
    string CardNumber,
    string? RecordId,
    string? CardHolderId,
    WinPakCardStatus Status,
    int Issue,
    DateTime? ActivationDate,
    DateTime? ExpirationDate,
    IReadOnlyList<string> AccessLevelIds);

public record WinPakCardHolder(string Id, string FirstName, string LastName, string? Note,
    IReadOnlyList<WinPakCard> Cards, IReadOnlyList<string> AccessLevelIds);

public record WinPakServerStatus(string ServerId, string ServerName, bool Connected, string? ServerType);

public record WinPakStatus(bool DatabaseServerConnected, IReadOnlyList<WinPakServerStatus> Servers, string? Error);

/// <summary>Režim dveří podle WIN-PAK.</summary>
public enum WinPakDoorMode
{
    Disabled = 1, Unlocked = 2, Locked = 3, SiteCodeOnly = 4,
    CardOnly = 5, PinOnly = 6, CardAndPin = 7, CardOrPin = 8,
}

public record WinPakDoorStatus(string Hid, string? DeviceName, bool? IsOpen, bool? IsShunted,
    bool? ForcedOpen, bool? Ajar, string? Account, string? SubAccount);

/// <summary>
/// HTTP klient pro WinPak Connector (src/Acs.WinPakConnector) běžící na WIN-PAK serveru.
/// Adresa a API klíč se konfigurují v GUI (Nastavení → WIN-PAK).
/// </summary>
public class WinPakClient(HttpClient httpClient, SettingsService settings)
{
    // URL i klíč se sestavují per request — HttpClient nedovoluje měnit
    // BaseAddress/hlavičky po odeslání prvního požadavku (a klient je sdílený).
    private async Task<HttpRequestMessage> BuildRequestAsync(HttpMethod method, string path, CancellationToken ct)
    {
        var baseUrl = await settings.GetAsync(SettingKeys.WinPakBaseUrl, ct)
            ?? throw new InvalidOperationException("WIN-PAK konektor není nakonfigurován (Nastavení → WIN-PAK).");
        var request = new HttpRequestMessage(method, new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path));
        if (await settings.GetAsync(SettingKeys.WinPakApiKey, ct) is { } apiKey)
            request.Headers.Add("X-Api-Key", apiKey);
        return request;
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        using var request = await BuildRequestAsync(HttpMethod.Get, path, ct);
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(ct);
    }

    private async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = await BuildRequestAsync(method, path, ct);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public Task<WinPakInfo?> GetInfoAsync(CancellationToken ct = default)
        => GetAsync<WinPakInfo>("api/v1/info", ct);

    /// <summary>Stav spojení konektoru s databázovým a komunikačním serverem WIN-PAK.</summary>
    public Task<WinPakStatus?> GetStatusAsync(CancellationToken ct = default)
        => GetAsync<WinPakStatus>("api/v1/status", ct);

    public async Task<IReadOnlyList<WinPakReader>> GetReadersAsync(CancellationToken ct = default)
        => await GetAsync<List<WinPakReader>>("api/v1/readers", ct) ?? [];

    public async Task<IReadOnlyList<WinPakAccessLevel>> GetAccessLevelsAsync(CancellationToken ct = default)
        => await GetAsync<List<WinPakAccessLevel>>("api/v1/access-levels", ct) ?? [];

    /// <summary>Strom přístupů úrovně (čtečky a časové zóny) tak, jak ho vrací WIN-PAK — text, jehož podobu určuje instalace.</summary>
    public async Task<string?> GetAccessTreeAsync(string accessLevelName, CancellationToken ct = default)
    {
        var response = await GetAsync<AccessTreeResponse>($"api/v1/access-levels/{Uri.EscapeDataString(accessLevelName)}/tree", ct);
        return response?.AccessTree;
    }

    private sealed record AccessTreeResponse(string? AccessTree);

    public async Task<IReadOnlyList<WinPakTimeZone>> GetTimeZonesAsync(CancellationToken ct = default)
        => await GetAsync<List<WinPakTimeZone>>("api/v1/time-zones", ct) ?? [];

    /// <summary>
    /// Založí (<paramref name="accessLevelId"/> null) nebo přepíše přístupovou úroveň
    /// včetně čteček a časových zón jedním zápisem. WIN-PAK přepíše celou definici —
    /// čtečky, které v seznamu nejsou, z úrovně zmizí.
    /// </summary>
    public Task UpsertAccessLevelAsync(string? accessLevelId, WinPakAccessLevelDefinition definition, CancellationToken ct = default)
        => SendAsync(HttpMethod.Put, $"api/v1/access-levels/{Uri.EscapeDataString(accessLevelId ?? "0")}", definition, ct);

    public Task DeleteAccessLevelAsync(string accessLevelName, CancellationToken ct = default)
        => SendAsync(HttpMethod.Delete, $"api/v1/access-levels/{Uri.EscapeDataString(accessLevelName)}", null, ct);

    /// <summary>Načte držitele karet včetně jejich aktuálních access levelů (zpětná synchronizace).</summary>
    public async Task<IReadOnlyList<WinPakCardHolder>> GetCardHoldersAsync(CancellationToken ct = default)
        => await GetAsync<List<WinPakCardHolder>>("api/v1/cardholders", ct) ?? [];

    /// <summary>Přiřadí access level držiteli karty (fronta správce karet → „Předat do systému“).</summary>
    public async Task AssignAccessLevelAsync(string cardHolderId, string accessLevelId, CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Post,
            $"api/v1/cardholders/{Uri.EscapeDataString(cardHolderId)}/access-levels", ct);
        request.Content = JsonContent.Create(new { accessLevelId });
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task RevokeAccessLevelAsync(string cardHolderId, string accessLevelId, CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Delete,
            $"api/v1/cardholders/{Uri.EscapeDataString(cardHolderId)}/access-levels/{Uri.EscapeDataString(accessLevelId)}", ct);
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Karta podle čísla — null, pokud ji WIN-PAK nezná.</summary>
    public async Task<WinPakCard?> GetCardAsync(string cardNumber, CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Get,
            $"api/v1/cards/{Uri.EscapeDataString(cardNumber)}", ct);
        using var response = await httpClient.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<WinPakCard>(ct);
    }

    /// <summary>Založí kartu, nebo upraví existující (WIN-PAK na to má jediné volání).</summary>
    public Task UpsertCardAsync(string cardNumber, string? cardHolderId, WinPakCardStatus status,
        DateTime? activationDate = null, DateTime? expirationDate = null,
        IReadOnlyList<string>? accessLevelIds = null, CancellationToken ct = default)
        => SendAsync(HttpMethod.Put, $"api/v1/cards/{Uri.EscapeDataString(cardNumber)}",
            new { cardHolderId, status, activationDate, expirationDate, accessLevelIds }, ct);

    public Task<WinPakDoorStatus?> GetDoorStatusAsync(long hid, CancellationToken ct = default)
        => GetAsync<WinPakDoorStatus>($"api/v1/doors/{hid}", ct);

    /// <summary>Krátce otevře dveře; bez délky se použije výchozí puls panelu.</summary>
    public Task PulseDoorAsync(long hid, int? seconds = null, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, $"api/v1/doors/{hid}/pulse", new { seconds }, ct);

    public Task LockDoorAsync(long hid, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, $"api/v1/doors/{hid}/lock", null, ct);

    public Task UnlockDoorAsync(long hid, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, $"api/v1/doors/{hid}/unlock", null, ct);

    public Task SetDoorModeAsync(long hid, WinPakDoorMode mode, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, $"api/v1/doors/{hid}/mode", new { mode }, ct);
}
