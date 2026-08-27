using System.Net.Http.Json;
using Acs.Infrastructure.Settings;

namespace Acs.Infrastructure.WinPak;

public record WinPakReader(string Id, string Name, string? Description, string? PanelName, string? AccountName, bool IsActive);
public record WinPakAccessLevel(string Id, string Name, string? Description);
public record WinPakInfo(string Version, string ProviderMode, bool SupportsWrite);
public record WinPakCard(string CardNumber, string? Status, DateTime? ActivationDate, DateTime? ExpirationDate);
public record WinPakCardHolder(string Id, string FirstName, string LastName, string? Note,
    IReadOnlyList<WinPakCard> Cards, IReadOnlyList<string> AccessLevelIds);

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

    public Task<WinPakInfo?> GetInfoAsync(CancellationToken ct = default)
        => GetAsync<WinPakInfo>("api/v1/info", ct);

    public async Task<IReadOnlyList<WinPakReader>> GetReadersAsync(CancellationToken ct = default)
        => await GetAsync<List<WinPakReader>>("api/v1/readers", ct) ?? [];

    public async Task<IReadOnlyList<WinPakAccessLevel>> GetAccessLevelsAsync(CancellationToken ct = default)
        => await GetAsync<List<WinPakAccessLevel>>("api/v1/access-levels", ct) ?? [];

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
}
