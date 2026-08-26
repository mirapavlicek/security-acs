using System.Net.Http.Json;
using Acs.Infrastructure.Settings;

namespace Acs.Infrastructure.WinPak;

public record WinPakReader(string Id, string Name, string? Description, string? PanelName, string? AccountName, bool IsActive);
public record WinPakAccessLevel(string Id, string Name, string? Description);
public record WinPakInfo(string Version, string ProviderMode, bool SupportsWrite);

/// <summary>
/// HTTP klient pro WinPak Connector (src/Acs.WinPakConnector) běžící na WIN-PAK serveru.
/// Adresa a API klíč se konfigurují v GUI (Nastavení → WIN-PAK).
/// </summary>
public class WinPakClient(HttpClient httpClient, SettingsService settings)
{
    private async Task<HttpClient> PrepareAsync(CancellationToken ct)
    {
        var baseUrl = await settings.GetAsync(SettingKeys.WinPakBaseUrl, ct)
            ?? throw new InvalidOperationException("WIN-PAK konektor není nakonfigurován (Nastavení → WIN-PAK).");
        httpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        httpClient.DefaultRequestHeaders.Remove("X-Api-Key");
        if (await settings.GetAsync(SettingKeys.WinPakApiKey, ct) is { } apiKey)
            httpClient.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return httpClient;
    }

    public async Task<WinPakInfo?> GetInfoAsync(CancellationToken ct = default)
    {
        var client = await PrepareAsync(ct);
        return await client.GetFromJsonAsync<WinPakInfo>("api/v1/info", ct);
    }

    public async Task<IReadOnlyList<WinPakReader>> GetReadersAsync(CancellationToken ct = default)
    {
        var client = await PrepareAsync(ct);
        return await client.GetFromJsonAsync<List<WinPakReader>>("api/v1/readers", ct) ?? [];
    }

    public async Task<IReadOnlyList<WinPakAccessLevel>> GetAccessLevelsAsync(CancellationToken ct = default)
    {
        var client = await PrepareAsync(ct);
        return await client.GetFromJsonAsync<List<WinPakAccessLevel>>("api/v1/access-levels", ct) ?? [];
    }

    /// <summary>Přiřadí access level držiteli karty (fronta správce karet → „Předat do systému“).</summary>
    public async Task AssignAccessLevelAsync(string cardHolderId, string accessLevelId, CancellationToken ct = default)
    {
        var client = await PrepareAsync(ct);
        var response = await client.PostAsJsonAsync(
            $"api/v1/cardholders/{Uri.EscapeDataString(cardHolderId)}/access-levels",
            new { accessLevelId }, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task RevokeAccessLevelAsync(string cardHolderId, string accessLevelId, CancellationToken ct = default)
    {
        var client = await PrepareAsync(ct);
        var response = await client.DeleteAsync(
            $"api/v1/cardholders/{Uri.EscapeDataString(cardHolderId)}/access-levels/{Uri.EscapeDataString(accessLevelId)}", ct);
        response.EnsureSuccessStatusCode();
    }
}
