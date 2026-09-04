using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Acs.Infrastructure.Audit;
using Microsoft.Extensions.Configuration;

namespace Acs.Infrastructure.WinPak;

/// <summary>Vydaná verze konektoru na GitHubu s balíkem pro Windows.</summary>
public record ConnectorRelease(string Version, string Name, DateTime? PublishedAt, string PackageUrl, string? Sha256Url, long PackageSize, string? Notes);

/// <summary>
/// Aktualizace WIN-PAK konektoru z ACS. WIN-PAK server nemá přístup na internet;
/// aplikační servery ACS ano (odtud se aktualizuje i ACS). ACS proto release
/// stáhne z GitHubu, ověří SHA-256 a pošle ho konektoru na port 52001 — konektor
/// vymění soubory sám. Správce tak konektor aktualizuje z prohlížeče, bez RDP.
/// </summary>
public class ConnectorReleaseService(IHttpClientFactory httpClientFactory, WinPakClient winPak, AuditService audit, IConfiguration configuration)
{
    public const string HttpClientName = "GitHubReleases";

    private string Repository => configuration["ConnectorUpdate:Repository"] ?? "mirapavlicek/security-acs";

    public async Task<IReadOnlyList<ConnectorRelease>> ListReleasesAsync(int limit = 10, CancellationToken ct = default)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases?per_page={limit}");
        request.Headers.UserAgent.ParseAdd("acs-connector-updater");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var releases = await response.Content.ReadFromJsonAsync<List<GitHubRelease>>(ct) ?? [];

        return releases
            .Where(r => !r.Draft)
            .Select(r =>
            {
                var package = r.Assets.FirstOrDefault(a => a.Name.StartsWith("AcsWinPakConnector-", StringComparison.OrdinalIgnoreCase)
                                                           && a.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase));
                if (package is null)
                    return null;
                var sha = r.Assets.FirstOrDefault(a => a.Name.Equals(package.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
                return new ConnectorRelease(r.TagName.TrimStart('v'), r.Name ?? r.TagName, r.PublishedAt, package.BrowserDownloadUrl, sha?.BrowserDownloadUrl, package.Size, r.Body);
            })
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();
    }

    /// <summary>Stáhne balík dané verze, ověří SHA-256 podle souboru u releasu a pošle ho konektoru.</summary>
    public async Task<WinPakUpdateStatus?> PushReleaseAsync(string? userName, string version, bool start = true, CancellationToken ct = default)
    {
        var release = (await ListReleasesAsync(30, ct)).FirstOrDefault(r => r.Version == version)
                      ?? throw new InvalidOperationException($"Release {version} s balíkem konektoru na GitHubu není.");

        var http = httpClientFactory.CreateClient(HttpClientName);
        var temp = Path.Combine(Path.GetTempPath(), $"acs-connector-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var file = File.Create(temp))
            await using (var download = await http.GetStreamAsync(release.PackageUrl, ct))
                await download.CopyToAsync(file, ct);

            string sha256;
            await using (var file = File.OpenRead(temp))
                sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));

            if (release.Sha256Url is not null)
            {
                var expected = (await http.GetStringAsync(release.Sha256Url, ct)).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
                if (expected != sha256)
                    throw new InvalidOperationException($"Stažený balík {version} má jiný SHA-256 než release ({sha256} ≠ {expected}) — nasazení zastaveno.");
            }

            await using var package = File.OpenRead(temp);
            var status = await winPak.PushUpdateAsync(package, sha256, start, ct);
            await audit.LogAsync(userName, "connector-update-pushed", "WinPakConnector", version,
                $"balík {version} ({release.PackageSize / 1024 / 1024} MB) poslán konektoru{(start ? ", aktualizace spuštěna" : "")}", ct);
            return status;
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>Balík dodaný správcem (bez GitHubu): spočítá SHA-256 a pošle ho konektoru.</summary>
    public async Task<WinPakUpdateStatus?> PushPackageAsync(string? userName, Stream package, bool start = true, CancellationToken ct = default)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"acs-connector-upload-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var file = File.Create(temp))
                await package.CopyToAsync(file, ct);

            string sha256;
            await using (var file = File.OpenRead(temp))
                sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));

            await using var upload = File.OpenRead(temp);
            var status = await winPak.PushUpdateAsync(upload, sha256, start, ct);
            await audit.LogAsync(userName, "connector-update-pushed", "WinPakConnector", status?.Staged?.Version,
                $"vlastní balík poslán konektoru{(start ? ", aktualizace spuštěna" : "")}", ct);
            return status;
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("published_at")] DateTime? PublishedAt,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("assets")] List<GitHubAsset> Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);
}
