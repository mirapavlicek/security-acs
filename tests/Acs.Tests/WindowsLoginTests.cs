using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Acs.Domain.Entities;
using Acs.Infrastructure.Auth;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acs.Tests;

/// <summary>Přihlášení účtem Windows (Negotiate) — chování endpointu a přihlašovací stránky přes HTTP.</summary>
public class WindowsLoginTests(AcsWebFactory factory) : IClassFixture<AcsWebFactory>
{
    private async Task SetSsoAsync(bool enabled, bool autoLogin = false)
    {
        using var scope = factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        await settings.SetAsync(SettingKeys.SsoEnabled, enabled ? "true" : "false");
        await settings.SetAsync(SettingKeys.SsoAutoLogin, autoLogin ? "true" : "false");
    }

    private HttpClient Client() =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task WindowsLogin_vypnute_presmeruje_na_formular()
    {
        await SetSsoAsync(enabled: false);

        var response = await Client().GetAsync("/Account/WindowsLogin?returnUrl=%2FRequests");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.Contains("/Account/Login", location);
        Assert.Contains("manual=1", location);
        Assert.Contains("sso=disabled", location);
        Assert.Contains("returnUrl=%2FRequests", location);
    }

    [Fact]
    public async Task WindowsLogin_zapnute_vyzve_prohlizec_Negotiate_a_nabidne_formular()
    {
        await SetSsoAsync(enabled: true);

        var response = await Client().GetAsync("/Account/WindowsLogin");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Negotiate");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Přihlásit jménem a heslem", body);
        Assert.Contains("/Account/Login?manual=1", body);
    }

    [Fact]
    public async Task WindowsLogin_s_neplatnym_tokenem_skonci_na_formulari_a_ne_chybou_500()
    {
        await SetSsoAsync(enabled: true);

        var client = Client();
        var request = new HttpRequestMessage(HttpMethod.Get, "/Account/WindowsLogin?returnUrl=%2FRequests");
        request.Headers.Authorization = new AuthenticationHeaderValue("Negotiate", Convert.ToBase64String("nesmysl"u8));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("/Account/Login?manual=1&sso=", location);
        Assert.Contains("returnUrl=%2FRequests", location);
    }

    [Fact]
    public async Task Neplatny_Negotiate_token_mimo_prihlaseni_neshodi_pozadavek()
    {
        await SetSsoAsync(enabled: true);

        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Authorization = new AuthenticationHeaderValue("Negotiate", Convert.ToBase64String("nesmysl"u8));
        var response = await Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Prihlasovaci_stranka_s_automatickym_prihlasenim_presmeruje_na_WindowsLogin()
    {
        await SetSsoAsync(enabled: true, autoLogin: true);
        var client = Client();

        var auto = await client.GetAsync("/Account/Login?returnUrl=%2FRequests");
        Assert.Equal(HttpStatusCode.Redirect, auto.StatusCode);
        Assert.StartsWith("/Account/WindowsLogin", auto.Headers.Location!.ToString());
        Assert.Contains("returnUrl=%2FRequests", auto.Headers.Location!.ToString());

        var manual = await client.GetAsync("/Account/Login?manual=1");
        Assert.Equal(HttpStatusCode.OK, manual.StatusCode);
        var body = await manual.Content.ReadAsStringAsync();
        Assert.Contains("Přihlásit účtem Windows", body);
        Assert.Contains("name=\"Password\"", body);

        var failed = await client.GetStringAsync("/Account/Login?manual=1&sso=failed");
        Assert.Contains("Přihlášení účtem Windows se nezdařilo", failed);
    }

    [Fact]
    public async Task Prihlasovaci_stranka_bez_SSO_nema_tlacitko_Windows()
    {
        await SetSsoAsync(enabled: false);

        var body = await Client().GetStringAsync("/Account/Login");

        Assert.DoesNotContain("Přihlásit účtem Windows", body);
        Assert.Contains("name=\"Password\"", body);
    }

    [Fact]
    public async Task Odhlaseni_vede_na_formular_bez_automatickeho_prihlaseni()
    {
        await SetSsoAsync(enabled: true, autoLogin: true);
        const string password = "Znam3Heslo!Sso";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AcsDbContext>();
            var admin = await db.Users.FirstAsync(u => u.UserName == "admin");
            admin.PasswordHash = PasswordHasher.Hash(password);
            admin.MustChangePassword = false;
            await db.SaveChangesAsync();
        }

        var client = factory.CreateDefaultClient(new CookieContainerHandler());
        var loginPage = await client.GetStringAsync("/Account/Login?manual=1");
        var token = Regex.Match(loginPage, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var login = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["UserName"] = "admin", ["Password"] = password, ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var home = await client.GetStringAsync("/");
        var logoutToken = Regex.Match(home, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var logout = await client.PostAsync("/Account/Login?handler=Logout", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = logoutToken }));

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        var location = logout.Headers.Location!.ToString();
        Assert.Contains("manual=1", location);

        var afterLogout = await client.GetAsync(location);
        Assert.Equal(HttpStatusCode.OK, afterLogout.StatusCode);
        Assert.Contains("Byli jste odhlášeni", await afterLogout.Content.ReadAsStringAsync());
    }
}

/// <summary>Mapování ověřené identity Windows na uživatele ACS (bez HTTP).</summary>
public sealed class WindowsIdentityMappingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly SettingsService _settings;

    public WindowsIdentityMappingTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _settings = new SettingsService(_db, new EphemeralDataProtectionProvider());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private UserAuthenticationService Create(LdapUserInfo? ldapResult = null)
    {
        var dcLocator = new DcLocator(_settings, NullLogger<DcLocator>.Instance);
        var ldap = new StubLdap(_settings, dcLocator, ldapResult);
        return new UserAuthenticationService(_db, ldap, _settings, NullLogger<UserAuthenticationService>.Instance);
    }

    private sealed class StubLdap(SettingsService settings, DcLocator dcLocator, LdapUserInfo? result)
        : LdapAuthenticator(settings, dcLocator, NullLogger<LdapAuthenticator>.Instance)
    {
        public string? LookedUp { get; private set; }

        public override Task<LdapUserInfo?> LookupAsync(string samAccount, CancellationToken ct = default)
        {
            LookedUp = samAccount;
            return Task.FromResult(result);
        }
    }

    [Theory]
    [InlineData(@"FNMH\novak", "novak", "FNMH")]
    [InlineData("novak@FNMH.LOCAL", "novak", "FNMH.LOCAL")]
    [InlineData("novak", "novak", null)]
    [InlineData(@"  FNMH\Novak  ", "Novak", "FNMH")]
    public void Jmeno_identity_se_rozlozi_na_ucet_a_domenu(string identity, string account, string? domain)
    {
        var parsed = WindowsAccountName.Parse(identity);
        Assert.Equal(account, parsed.Account);
        Assert.Equal(domain, parsed.Domain);
    }

    [Theory]
    [InlineData(@"FNMH\novak", "", true)]
    [InlineData(@"FNMH\novak", "FNMH", true)]
    [InlineData(@"FNMH\novak", "fnmh", true)]
    [InlineData("novak@FNMH.LOCAL", "FNMH", true)]
    [InlineData("novak@FNMH.LOCAL", "fnmh.local", true)]
    [InlineData("novak@FNMH.LOCAL", "NNH, fnmh.local", true)]
    [InlineData(@"NNH\novak", "FNMH", false)]
    [InlineData("novak@nnh.local", "fnmh.local", false)]
    [InlineData("novak", "FNMH", false)]
    public void Povolene_domeny_se_porovnavaji_NetBIOS_i_DNS_tvarem(string identity, string allowed, bool expected)
        => Assert.Equal(expected, WindowsAccountName.Parse(identity).IsDomainAllowed(allowed));

    [Fact]
    public async Task Prvni_prihlaseni_zalozi_uzivatele_a_spáruje_zamestnance_podle_AD_uctu()
    {
        _db.Employees.Add(new Employee { PersonalNumber = "1001", FirstName = "Jan", LastName = "Novák", AdAccount = "Novak" });
        await _db.SaveChangesAsync();

        var user = await Create().SignInWindowsIdentityAsync(@"FNMH\novak");

        Assert.NotNull(user);
        Assert.False(user.IsLocal);
        Assert.Equal("novak", user.UserName);
        Assert.Equal(AppRole.Employee, user.Roles);
        Assert.NotNull(user.EmployeeId);
        Assert.NotNull(user.LastLoginAt);
    }

    [Fact]
    public async Task Opakovane_prihlaseni_najde_uzivatele_bez_ohledu_na_velikost_pismen_a_tvar_jmena()
    {
        _db.Users.Add(new AppUser { UserName = "Novak", IsLocal = false, Roles = AppRole.Employee | AppRole.Approver, DisplayName = "Jan Novák" });
        await _db.SaveChangesAsync();

        var user = await Create().SignInWindowsIdentityAsync("novak@FNMH.LOCAL");

        Assert.NotNull(user);
        Assert.Equal("Novak", user.UserName);
        // Bez načtení z AD (servisní účet není) se role ani jméno nemění.
        Assert.Equal(AppRole.Employee | AppRole.Approver, user.Roles);
        Assert.Equal("Jan Novák", user.DisplayName);
        Assert.Equal(1, await _db.Users.CountAsync(u => !u.IsLocal));
    }

    [Fact]
    public async Task Atributy_a_role_se_nactou_z_AD_servisnim_uctem()
    {
        await _settings.SetAsync(SettingKeys.LdapGroupRoleMap, "ACS-Spravci=Admin\nACS-Schvalovatele=Approver");
        var ldapInfo = new LdapUserInfo("novak", "Jan Novák", "jan.novak@fnmh.cz",
            ["CN=ACS-Schvalovatele,OU=Skupiny,DC=fnmh,DC=local"]);

        var user = await Create(ldapInfo).SignInWindowsIdentityAsync(@"FNMH\novak");

        Assert.NotNull(user);
        Assert.Equal("Jan Novák", user.DisplayName);
        Assert.Equal("jan.novak@fnmh.cz", user.Email);
        Assert.Equal(AppRole.Employee | AppRole.Approver, user.Roles);
    }

    [Fact]
    public async Task Neaktivni_uzivatel_se_neprihlasi()
    {
        _db.Users.Add(new AppUser { UserName = "novak", IsLocal = false, Roles = AppRole.Employee, IsActive = false });
        await _db.SaveChangesAsync();

        Assert.Null(await Create().SignInWindowsIdentityAsync(@"FNMH\novak"));
    }

    [Fact]
    public async Task Ucet_z_nepovolene_domeny_a_ucet_stroje_se_neprihlasi()
    {
        await _settings.SetAsync(SettingKeys.SsoAllowedDomains, "FNMH");
        var service = Create();

        Assert.Null(await service.SignInWindowsIdentityAsync(@"NNH\novak"));
        Assert.Null(await service.SignInWindowsIdentityAsync(@"FNMH\PC-0815$"));
        Assert.NotNull(await service.SignInWindowsIdentityAsync(@"FNMH\novak"));
        Assert.Equal(1, await _db.Users.CountAsync(u => !u.IsLocal));
    }

    [Fact]
    public async Task Domenovy_ucet_se_jmenem_lokalniho_uctu_se_pres_Windows_neprihlasi()
    {
        _db.Users.Add(new AppUser { UserName = "admin", IsLocal = true, Roles = AppRole.Admin, PasswordHash = PasswordHasher.Hash("x") });
        await _db.SaveChangesAsync();

        // Lokální admin se přihlašuje jen heslem; doménový „admin“ by převzal jeho práva.
        Assert.Null(await Create().SignInWindowsIdentityAsync(@"FNMH\admin"));
        Assert.Equal(1, await _db.Users.CountAsync());
    }
}
