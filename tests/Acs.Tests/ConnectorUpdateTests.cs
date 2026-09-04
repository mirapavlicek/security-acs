using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.WinPak;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Acs.Tests;

/// <summary>
/// Aktualizace konektoru z ACS: seznam vydání z GitHubu, stažení a ověření balíku
/// a jeho odeslání konektoru na port 52001. GitHub i konektor nahrazují HTTP atrapy.
/// </summary>
public sealed class ConnectorUpdateTests : IDisposable
{
    private sealed class Router : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpRequestMessage, Task<HttpResponseMessage>>> Routes { get; } = new();
        public List<(HttpRequestMessage Request, byte[] Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            Requests.Add((request, body));
            var key = Routes.Keys.FirstOrDefault(k => request.RequestUri!.ToString().Contains(k));
            return key is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : await Routes[key](request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Bytes(byte[] value) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AcsDbContext _db;
    private readonly Router _router = new();
    private readonly ConnectorReleaseService _service;
    private static readonly byte[] Package = Encoding.UTF8.GetBytes("PK-fake-connector-package");
    private static readonly string PackageSha = Convert.ToHexStringLower(SHA256.HashData(Package));

    public ConnectorUpdateTests()
    {
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        var settings = new SettingsService(_db, new EphemeralDataProtectionProvider());
        settings.SetAsync(SettingKeys.WinPakBaseUrl, "http://connector:52001").GetAwaiter().GetResult();
        settings.SetAsync(SettingKeys.WinPakApiKey, "k").GetAwaiter().GetResult();

        _router.Routes["api.github.com/repos/test/repo/releases"] = _ => Task.FromResult(Json(new object[]
        {
            new
            {
                tag_name = "v1.12.9", name = "WIN-PAK konektor v1.12.9", draft = false, published_at = "2026-09-04T07:36:39Z", body = "popis",
                assets = new object[]
                {
                    new { name = "AcsWinPakConnector-1.12.9-win-x64.zip", size = Package.Length, browser_download_url = "https://dl.test/1.12.9.zip" },
                    new { name = "AcsWinPakConnector-1.12.9-win-x64.zip.sha256", size = 100, browser_download_url = "https://dl.test/1.12.9.sha256" },
                },
            },
            new { tag_name = "v1.12.8", name = "bez balíku", draft = false, published_at = "2026-09-03T20:28:00Z", body = "", assets = new object[0] },
            new
            {
                tag_name = "v1.13.0-draft", name = "koncept", draft = true, published_at = (string?)null, body = "",
                assets = new object[] { new { name = "AcsWinPakConnector-1.13.0-win-x64.zip", size = 1, browser_download_url = "https://dl.test/x.zip" } },
            },
        }));
        _router.Routes["dl.test/1.12.9.zip"] = _ => Task.FromResult(Bytes(Package));
        _router.Routes["dl.test/1.12.9.sha256"] = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{PackageSha}  AcsWinPakConnector-1.12.9-win-x64.zip\n"),
        });
        _router.Routes["connector:52001/api/v1/update"] = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                currentVersion = "1.12.8", supported = true, startedUtc = (string?)null, log = (string?)null,
                staged = new { version = "1.12.9", path = "C:\\\\x.zip", size = Package.Length, sha256 = PackageSha, receivedUtc = "2026-09-04T08:00:00Z" },
            }), Encoding.UTF8, "application/json"),
        });

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectorUpdate:Repository"] = "test/repo" })
            .Build();
        var client = new WinPakClient(new HttpClient(_router), settings);
        _service = new ConnectorReleaseService(new Factory(_router), client, new AuditService(_db), configuration);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Seznam_vydani_bere_jen_ta_s_balikem_konektoru_a_bez_konceptu()
    {
        var releases = await _service.ListReleasesAsync();

        var release = Assert.Single(releases);
        Assert.Equal("1.12.9", release.Version);
        Assert.Equal("https://dl.test/1.12.9.zip", release.PackageUrl);
        Assert.Equal("https://dl.test/1.12.9.sha256", release.Sha256Url);
        Assert.Equal(Package.Length, release.PackageSize);
    }

    [Fact]
    public async Task Nasazeni_vydani_stahne_overi_a_posle_balik_konektoru_s_hashem()
    {
        var status = await _service.PushReleaseAsync("admin", "1.12.9");

        Assert.Equal("1.12.9", status?.Staged?.Version);
        var push = _router.Requests.Single(r => r.Request.RequestUri!.ToString().Contains("/api/v1/update"));
        Assert.Equal(HttpMethod.Post, push.Request.Method);
        Assert.EndsWith("/api/v1/update?start=true", push.Request.RequestUri!.ToString());
        Assert.Equal(PackageSha, push.Request.Headers.GetValues("X-Package-Sha256").Single());
        Assert.Equal("k", push.Request.Headers.GetValues("X-Api-Key").Single());
        Assert.Equal(Package, push.Body);
        Assert.Contains(await _db.AuditLogs.ToListAsync(), a => a.Action == "connector-update-pushed");
    }

    [Fact]
    public async Task Balik_s_jinym_hashem_nez_u_vydani_se_konektoru_neposle()
    {
        _router.Routes["dl.test/1.12.9.sha256"] = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('f', 64)),
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.PushReleaseAsync("admin", "1.12.9"));

        Assert.Contains("jiný SHA-256", error.Message);
        Assert.DoesNotContain(_router.Requests, r => r.Request.RequestUri!.ToString().Contains("/api/v1/update"));
    }

    [Fact]
    public async Task Odmitnuti_konektorem_se_prelozi_na_srozumitelnou_chybu()
    {
        _router.Routes["connector:52001/api/v1/update"] = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("{\"error\":\"SHA-256 balíku nesouhlasí\"}", Encoding.UTF8, "application/json"),
        });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => _service.PushPackageAsync("admin", new MemoryStream(Package)));

        Assert.Contains("422", error.Message);
        Assert.Contains("SHA-256 balíku nesouhlasí", error.Message);
    }
}
