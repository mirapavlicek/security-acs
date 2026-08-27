using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Acs.WinPakConnector.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Acs.WinPakConnector.Tests;

/// <summary>
/// Administrační GUI běží na WIN-PAK serveru a mění nastavení služby, takže se
/// testuje i to, že se bez přihlášení nikam nedostane a že se změny opravdu použijí.
/// Každý test má vlastní dočasný adresář, aby si testy nepřepisovaly nastavení.
/// </summary>
public sealed class AdminUiFactory : WebApplicationFactory<Program>, IDisposable
{
    public const string ApiKey = "test-api-key-0123456789";

    private readonly string _contentRoot = Directory.CreateTempSubdirectory("winpak-ui-").FullName;

    public string SettingsFile => Path.Combine(_contentRoot, "appsettings.Local.json");

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Security:ApiKey"] = ApiKey,
                ["WinPak:Mode"] = "Mock",
            }));
        builder.ConfigureWebHost(web => web.UseContentRoot(_contentRoot));
        return base.CreateHost(builder);
    }

    public new void Dispose()
    {
        base.Dispose();
        if (Directory.Exists(_contentRoot))
            Directory.Delete(_contentRoot, recursive: true);
    }
}

public sealed class AdminUiTests(AdminUiFactory factory) : IClassFixture<AdminUiFactory>
{
    private HttpClient CreateClient(bool followRedirects = false)
        => factory.CreateDefaultClient(followRedirects
            ? [new RedirectHandler(7), new CookieContainerHandler()]
            : [new CookieContainerHandler()]);

    /// <summary>Přihlásí se do GUI a vrátí klienta s platnou cookie.</summary>
    private async Task<HttpClient> SignInAsync(string password = AdminUiFactory.ApiKey)
    {
        var client = CreateClient(followRedirects: true);
        var loginPage = await client.GetStringAsync("/ui/login");

        var response = await client.PostAsync("/ui/login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Password"] = password,
                ["__RequestVerificationToken"] = ExtractToken(loginPage),
            }));
        response.EnsureSuccessStatusCode();
        return client;
    }

    private static string ExtractToken(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Antiforgery token nenalezen.");
        return match.Groups[1].Value;
    }

    [Fact]
    public async Task Korenova_adresa_vede_do_administrace()
    {
        var response = await CreateClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/ui", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Administrace_bez_prihlaseni_presmeruje_na_login()
    {
        foreach (var path in new[] { "/ui", "/ui/settings", "/ui/diagnostics" })
        {
            var response = await CreateClient().GetAsync(path);

            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Contains("/ui/login", response.Headers.Location?.ToString());
        }
    }

    [Fact]
    public async Task Prihlaseni_spatnym_heslem_skonci_chybou()
    {
        var client = CreateClient(followRedirects: true);
        var loginPage = await client.GetStringAsync("/ui/login");

        var response = await client.PostAsync("/ui/login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Password"] = "spatne",
                ["__RequestVerificationToken"] = ExtractToken(loginPage),
            }));

        Assert.Contains("Neplatné heslo", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Po_prihlaseni_je_prehled_dostupny_a_klic_je_maskovany()
    {
        var client = await SignInAsync();

        var page = await client.GetStringAsync("/ui");

        Assert.Contains("Přehled konektoru", page);
        // Klíč se nikdy nesmí objevit celý; maska ukazuje jen okraje.
        Assert.DoesNotContain(AdminUiFactory.ApiKey, page);
        Assert.Contains("test", page);
    }

    [Fact]
    public async Task Diagnostika_spusti_kontroly_proti_provideru()
    {
        var client = await SignInAsync();

        var page = await client.GetStringAsync("/ui/diagnostics");

        Assert.Contains("Čtečky", page);
        Assert.Contains("Přístupové úrovně", page);
        Assert.Contains("8 čteček", page);
    }

    [Fact]
    public async Task Nastaveni_nabizi_vsechny_rezimy_a_neprozradi_tajne_hodnoty()
    {
        var client = await SignInAsync();

        var page = await client.GetStringAsync("/ui/settings");

        Assert.Contains("value=\"Mock\"", page);
        Assert.Contains("value=\"Mssql\"", page);
        Assert.Contains("value=\"Com\"", page);
        Assert.DoesNotContain(AdminUiFactory.ApiKey, page);
    }

    [Fact]
    public async Task Neuplne_nastaveni_rezimu_Com_se_neulozi()
    {
        var client = await SignInAsync();
        var page = await client.GetStringAsync("/ui/settings");

        var response = await client.PostAsync("/ui/settings?handler=Save", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Input.Mode"] = "Com",
                ["Input.Com.UserName"] = "",
                ["Input.Com.ApplicationProgId"] = "NCIHelper.Application",
                ["__RequestVerificationToken"] = ExtractToken(page),
            }));

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Nastavení nebylo uloženo", html);
        Assert.Contains("Zadejte operátora WIN-PAK", html);

        // Režim zůstal původní, protože se nic neuložilo.
        var info = await ApiInfoAsync();
        Assert.Equal("Mock", info.ProviderMode);
    }

    [Fact]
    public async Task Ulozene_nastaveni_se_zapise_do_souboru_a_provider_se_prestavi()
    {
        var client = await SignInAsync();
        var page = await client.GetStringAsync("/ui/settings");

        var response = await client.PostAsync("/ui/settings?handler=Save", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Input.Mode"] = "Mssql",
                ["Input.Mssql.ConnectionString"] = "Server=localhost;Database=WINPAK;Integrated Security=true",
                ["__RequestVerificationToken"] = ExtractToken(page),
            }));

        Assert.Contains("Nastavení uloženo", await response.Content.ReadAsStringAsync());
        Assert.True(File.Exists(factory.SettingsFile));

        // Provider se musí přepnout bez restartu — pozná se podle /api/v1/info.
        var info = await ApiInfoAsync();
        Assert.Equal("Mssql", info.ProviderMode);
        Assert.False(info.SupportsWrite);

        await RestoreMockModeAsync(client);
    }

    [Fact]
    public async Task Heslo_administrace_prepne_prihlasovani_z_API_klice()
    {
        var client = await SignInAsync();
        var page = await client.GetStringAsync("/ui/settings");

        await client.PostAsync("/ui/settings?handler=Save", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Input.Mode"] = "Mock",
                ["Input.AdminPassword"] = "SpravneHeslo123",
                ["__RequestVerificationToken"] = ExtractToken(page),
            }));

        // Nová relace: API klíč už jako heslo neprojde, samostatné heslo ano.
        var fresh = CreateClient(followRedirects: true);
        var loginPage = await fresh.GetStringAsync("/ui/login");
        Assert.Contains("Heslo administrace", loginPage);

        var withKey = await fresh.PostAsync("/ui/login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Password"] = AdminUiFactory.ApiKey,
                ["__RequestVerificationToken"] = ExtractToken(loginPage),
            }));
        Assert.Contains("Neplatné heslo", await withKey.Content.ReadAsStringAsync());

        var withPassword = await SignInAsync("SpravneHeslo123");
        Assert.Contains("Přehled konektoru", await withPassword.GetStringAsync("/ui"));

        await RestoreMockModeAsync(withPassword, clearAdminPassword: true);
    }

    [Fact]
    public async Task Vygenerovany_klic_se_ukaze_ale_neulozi_se_sam()
    {
        var client = await SignInAsync();
        var page = await client.GetStringAsync("/ui/settings");

        var response = await client.PostAsync("/ui/settings?handler=GenerateKey", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Input.Mode"] = "Mock",
                ["__RequestVerificationToken"] = ExtractToken(page),
            }));

        var html = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "<p class=\"mono\">([0-9A-F]{64})</p>");
        Assert.True(match.Success, "Vygenerovaný klíč se nezobrazil.");

        // Dokud se neuloží, platí pořád původní klíč.
        var info = await ApiInfoAsync();
        Assert.Equal("Mock", info.ProviderMode);
    }

    [Fact]
    public async Task Rest_api_zustava_chranene_klicem_i_kdyz_je_uzivatel_prihlaseny_do_GUI()
    {
        var client = await SignInAsync();

        var response = await client.GetAsync("/api/v1/readers");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<ConnectorInfoDto> ApiInfoAsync()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", AdminUiFactory.ApiKey);
        return (await client.GetFromJsonAsync<ConnectorInfoDto>("/api/v1/info"))!;
    }

    /// <summary>Vrátí konektor do výchozího stavu, ať na sobě testy nezávisí.</summary>
    private async Task RestoreMockModeAsync(HttpClient client, bool clearAdminPassword = false)
    {
        var page = await client.GetStringAsync("/ui/settings");
        if (clearAdminPassword)
        {
            await client.PostAsync("/ui/settings?handler=ClearAdminPassword", new FormUrlEncodedContent(
                new Dictionary<string, string> { ["__RequestVerificationToken"] = ExtractToken(page) }));
            page = await client.GetStringAsync("/ui/settings");
        }

        await client.PostAsync("/ui/settings?handler=Save", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Input.Mode"] = "Mock",
                ["__RequestVerificationToken"] = ExtractToken(page),
            }));
    }
}
