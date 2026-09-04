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

    [Fact]
    public async Task ParkingPages_DeniedToAnonymous()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var url in new[] { "/Parking", "/Parking/New", "/Parking/Queue", "/Catalog/Parking/Sites", "/Catalog/Parking/PermitTypes" })
        {
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        }
    }

    [Fact]
    public async Task Admin_SeesParkingPages_AndCatalogs()
    {
        const string knownPassword = "Znam3Heslo!Park";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Acs.Infrastructure.Data.AcsDbContext>();
            var admin = await db.Users.FirstAsync(u => u.UserName == "admin");
            admin.PasswordHash = Acs.Infrastructure.Auth.PasswordHasher.Hash(knownPassword);
            admin.MustChangePassword = false;
            if (!await db.ParkingPermitTypes.AnyAsync())
            {
                db.ParkingPermitTypes.Add(new Acs.Domain.Entities.ParkingPermitType { Name = "Vedení nemocnice", Binding = Acs.Domain.Entities.PermitBinding.Function });
                db.Sites.Add(new Acs.Domain.Entities.Site { Name = "Motol" });
            }
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

        var home = await client.GetStringAsync("/");
        var mainNav = ExtractBlock(home, "class=\"mainnav\"", "</nav>");
        Assert.Contains("Parkování", mainNav);
        Assert.Contains("/Catalog/Parking/Sites", mainNav);
        Assert.Contains("/Catalog/Parking/PermitTypes", mainNav);

        Assert.Contains("Požádat o parkovací povolení", await client.GetStringAsync("/Parking"));
        var form = await client.GetStringAsync("/Parking/New");
        Assert.Contains("Vedení nemocnice", form);
        Assert.Contains("Motol", form);
        Assert.Contains("Fronta správce parkování", await client.GetStringAsync("/Parking/Queue"));
        Assert.Contains("Druhy parkovacích povolení", await client.GetStringAsync("/Catalog/Parking/PermitTypes"));
        Assert.Contains("Areály", await client.GetStringAsync("/Catalog/Parking/Sites"));
        Assert.Contains("Parkovací povolení", await client.GetStringAsync("/Reports?view=parking"));
    }

    [Fact]
    public async Task Hromadne_odstraneni_ctecek_pres_HTTP_skonci_presmerovanim_a_hlaskou()
    {
        const string knownPassword = "Znam3Heslo!Ctecky";
        int readerId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Acs.Infrastructure.Data.AcsDbContext>();
            var admin = await db.Users.FirstAsync(u => u.UserName == "admin");
            admin.PasswordHash = Acs.Infrastructure.Auth.PasswordHasher.Hash(knownPassword);
            admin.MustChangePassword = false;
            var reader = new Acs.Domain.Entities.Reader { Name = "Smazatelná", ExternalId = "DEL-1", Source = Acs.Domain.Entities.RecordSource.Imported, IsActive = true };
            db.Readers.Add(reader);
            await db.SaveChangesAsync();
            readerId = reader.Id;
        }

        var client = CreateClientWithCookies(allowRedirects: false);
        var loginPage = await client.GetStringAsync("/Account/Login");
        await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["UserName"] = "admin",
                ["Password"] = knownPassword,
                ["__RequestVerificationToken"] = ExtractAntiforgeryToken(loginPage),
            }));

        var page = await client.GetStringAsync("/Catalog/Readers");
        // „Označit vše“ nad celým číselníkem: tisíce hodnot readerIds — víc než výchozí limit 1 024 polí formuláře.
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", ExtractAntiforgeryToken(page)), new("readerIds", readerId.ToString()) };
        form.AddRange(Enumerable.Range(1_000_000, 2_500).Select(id => new KeyValuePair<string, string>("readerIds", id.ToString())));
        var response = await client.PostAsync("/Catalog/Readers?handler=Delete", new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith("/Catalog/Readers", response.Headers.Location!.ToString());
        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<Acs.Infrastructure.Data.AcsDbContext>();
        Assert.False(await db2.Readers.AnyAsync(r => r.Id == readerId));
    }

    [Fact]
    public async Task Chybove_odpovedi_maji_HTML_stranku_misto_prazdneho_tela()
    {
        var client = CreateClientWithCookies(allowRedirects: false);

        // Bez tokenu ochrany formuláře vrací Razor Pages 400 — dřív s prázdným tělem, které Safari stáhlo jako soubor.
        var badRequest = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["UserName"] = "x", ["Password"] = "y" }));
        Assert.Equal(HttpStatusCode.BadRequest, badRequest.StatusCode);
        Assert.Contains("text/html", badRequest.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Požadavek server nepřijal", await badRequest.Content.ReadAsStringAsync());

        // Neexistující stránka: anonymního uživatele nejdřív pošle na přihlášení, přihlášený dostane 404 se stránkou.
        const string knownPassword = "Znam3Heslo!Chyby";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Acs.Infrastructure.Data.AcsDbContext>();
            var admin = await db.Users.FirstAsync(u => u.UserName == "admin");
            admin.PasswordHash = Acs.Infrastructure.Auth.PasswordHasher.Hash(knownPassword);
            admin.MustChangePassword = false;
            await db.SaveChangesAsync();
        }

        var loginPage = await client.GetStringAsync("/Account/Login");
        await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["UserName"] = "admin",
                ["Password"] = knownPassword,
                ["__RequestVerificationToken"] = ExtractAntiforgeryToken(loginPage),
            }));

        var notFound = await client.GetAsync("/Catalog/Neexistuje");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Contains("Stránka nebyla nalezena", await notFound.Content.ReadAsStringAsync());
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
