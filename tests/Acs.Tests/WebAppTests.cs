using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Xunit;

namespace Acs.Tests;

public sealed class AcsWebFactory : WebApplicationFactory<Program>, IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"acs-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("Database:Provider", "Sqlite");
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
        var client = CreateClientWithCookies(allowRedirects: true);

        var loginPage = await client.GetStringAsync("/Account/Login");
        var token = ExtractAntiforgeryToken(loginPage);

        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["UserName"] = "admin",
                ["Password"] = "admin",
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

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = Regex.Match(html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Antiforgery token nenalezen.");
        return match.Groups[1].Value;
    }
}
