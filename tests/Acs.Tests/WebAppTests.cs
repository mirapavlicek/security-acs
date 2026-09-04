using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acs.Tests;

public sealed class AcsWebFactory : WebApplicationFactory<Program>, IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"acs-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        // Testy musí být deterministické i na stroji s produkčním prostředím
        // (updater na nodech spouští testy s nastaveným ASPNETCORE_ENVIRONMENT=Production,
        // což by zapnulo Secure cookies a rozbilo login test přes HTTP).
        builder.UseEnvironment("Development");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("Security:LoginRateLimit", "1000");
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={_dbPath}");
    }

    public new void Dispose()
    {
        base.Dispose();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }
}

public class WebAppTests(AcsWebFactory factory) : IClassFixture<AcsWebFactory>
{
    private HttpClient CreateClientWithCookies(bool allowRedirects = false)
    {
        DelegatingHandler[] handlers = allowRedirects
            ? [new RedirectHandler(7), new CookieContainerHandler()]
            : [new CookieContainerHandler()];
        return factory.CreateDefaultClient(handlers);
    }

    [Fact]
    public async Task Health_ReturnsOk_WithoutLogin()
    {
        var response = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Root_RedirectsAnonymousUser_ToLogin()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task LoginPage_IsAccessibleAnonymously()
    {
        var response = await factory.CreateClient().GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Přihlásit", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AdminSettings_DeniedToAnonymous()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/Admin/Settings");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
    }

    [Fact]
    public async Task DefaultAdmin_CanLogIn_AndIsForcedToChangePassword()
    {
        // Počáteční heslo admina je náhodné (bezpečnostní opatření) — pro test
        // ho nastavíme na známou hodnotu přímo v DB.
        const string knownPassword = "Znam3Heslo!Admin";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Acs.Infrastructure.Data.AcsDbContext>();
            var admin = await db.Users.FirstAsync(u => u.UserName == "admin");
            admin.PasswordHash = Acs.Infrastructure.Auth.PasswordHasher.Hash(knownPassword);
            await db.SaveChangesAsync();
        }

        var client = CreateClientWithCookies(allowRedirects: true);

        var loginPage = await client.GetStringAsync("/Account/Login");
        var token = ExtractAntiforgeryToken(loginPage);

        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["UserName"] = "admin",
                ["Password"] = knownPassword,
                ["__RequestVerificationToken"] = token,
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Změna hesla", body);
    }

    [Fact]
    public async Task Login_WithWrongPassword_ShowsError()
    {
        var client = CreateClientWithCookies(allowRedirects: true);

        var loginPage = await client.GetStringAsync("/Account/Login");
        var token = ExtractAntiforgeryToken(loginPage);

        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["UserName"] = "admin",
                ["Password"] = "spatne-heslo",
                ["__RequestVerificationToken"] = token,
            }));

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Neplatné přihlašovací údaje", body);
    }

    [Fact]
    public async Task Zastupy_Jsou_V_Menu_Uzivatele_A_Ne_V_Hlavni_Navigaci()
    {
        const string knownPassword = "Znam3Heslo!Menu";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Acs.Infrastructure.Data.AcsDbContext>();
            var admin = await db.Users.FirstAsync(u => u.UserName == "admin");
            admin.PasswordHash = Acs.Infrastructure.Auth.PasswordHasher.Hash(knownPassword);
            admin.MustChangePassword = false;
            await db.SaveChangesAsync();
        }

        var client = CreateClientWithCookies(allowRedirects: true);
        var loginPage = await client.GetStringAsync("/Account/Login");
        await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["UserName"] = "admin",
                ["Password"] = knownPassword,
                ["__RequestVerificationToken"] = ExtractAntiforgeryToken(loginPage),
            }));

        var body = await client.GetStringAsync("/");

        var mainNav = ExtractBlock(body, "class=\"mainnav\"", "</nav>");
        Assert.DoesNotContain("Zástupy", mainNav);

        var userMenu = ExtractBlock(body, "class=\"usermenu-panel\"", "</details>");
        Assert.Contains("/Deputies", userMenu);
        Assert.Contains("Zástupy", userMenu);
        Assert.Contains("Odhlásit", userMenu);
        // Lokální účet smí měnit heslo; u AD účtů se položka nenabízí.
        Assert.Contains("/Account/ChangePassword", userMenu);
    }

    [Fact]
    public async Task Stranky_pristupovych_urovni_se_otevrou_i_bez_konektoru()
    {
        const string knownPassword = "Znam3Heslo!Urovne";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Acs.Infrastructure.Data.AcsDbContext>();
            var admin = await db.Users.FirstAsync(u => u.UserName == "admin");
            admin.PasswordHash = Acs.Infrastructure.Auth.PasswordHasher.Hash(knownPassword);
            admin.MustChangePassword = false;
            if (!await db.AccessLevels.AnyAsync(a => a.ExternalId == "77"))
                db.AccessLevels.Add(new Acs.Domain.Entities.AccessLevel { ExternalId = "77", Name = "AL Test", Description = "z testu" });
            await db.SaveChangesAsync();
        }

        var client = CreateClientWithCookies(allowRedirects: true);
        var loginPage = await client.GetStringAsync("/Account/Login");
        await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["UserName"] = "admin",
                ["Password"] = knownPassword,
                ["__RequestVerificationToken"] = ExtractAntiforgeryToken(loginPage),
            }));

        var index = await client.GetStringAsync("/Catalog/AccessLevels");
        Assert.Contains("AL Test", index);
        Assert.Contains("Synchronizovat z WIN-PAK", index);
        Assert.Contains("/Catalog/AccessLevels", ExtractBlock(await client.GetStringAsync("/"), "class=\"mainnav\"", "</nav>"));

        // Bez nakonfigurovaného konektoru se časové zóny nenačtou — stránka to řekne a uložení zablokuje, ale otevře se.
        var edit = await client.GetStringAsync("/Catalog/AccessLevels/Edit");
        Assert.Contains("Nová přístupová úroveň", edit);
        Assert.Contains("Časové zóny se z WIN-PAKu nenačetly", edit);
    }

    [Fact]
    public async Task Stranka_aktualizace_konektoru_se_otevre_i_bez_konektoru()
    {
        const string knownPassword = "Znam3Heslo!Konektor";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Acs.Infrastructure.Data.AcsDbContext>();
            var admin = await db.Users.FirstAsync(u => u.UserName == "admin");
            admin.PasswordHash = Acs.Infrastructure.Auth.PasswordHasher.Hash(knownPassword);
            admin.MustChangePassword = false;
            await db.SaveChangesAsync();
        }

        var client = CreateClientWithCookies(allowRedirects: true);
        var loginPage = await client.GetStringAsync("/Account/Login");
        await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["UserName"] = "admin",
                ["Password"] = knownPassword,
                ["__RequestVerificationToken"] = ExtractAntiforgeryToken(loginPage),
            }));

        var page = await client.GetStringAsync("/Admin/ConnectorUpdate");

        Assert.Contains("WIN-PAK konektor", page);
        // Konektor není nakonfigurovaný — stránka to řekne, ale otevře se a nabídne vlastní balík.
        Assert.Contains("Konektor neodpovídá", page);
        Assert.Contains("Vlastní balík", page);
        Assert.Contains("/Admin/ConnectorUpdate", ExtractBlock(await client.GetStringAsync("/"), "class=\"mainnav\"", "</nav>"));
    }

    private static string ExtractBlock(string html, string start, string end)
    {
        var from = html.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"Blok '{start}' nenalezen.");
        var to = html.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"Konec bloku '{end}' nenalezen.");
        return html[from..to];
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = Regex.Match(html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Antiforgery token nenalezen.");
        return match.Groups[1].Value;
    }
}
