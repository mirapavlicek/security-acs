using System.Net;
using System.Text.RegularExpressions;
using Acs.Infrastructure.Auth;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Sync;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acs.Tests;

/// <summary>
/// Regrese: úloha na pozadí nesmí sahat na HttpContext (User) — ten je po odeslání
/// odpovědi uvolněný a operace by spadla na „IFeatureCollection has been disposed“.
/// </summary>
public class BackgroundSyncRegressionTests(AcsWebFactory factory) : IClassFixture<AcsWebFactory>
{
    private const string Password = "RegresTest2026!";

    private async Task<HttpClient> LoginAsAdminAsync()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AcsDbContext>();
            var admin = await db.Users.FirstAsync(u => u.UserName == "admin");
            admin.PasswordHash = PasswordHasher.Hash(Password);
            admin.MustChangePassword = false;
            await db.SaveChangesAsync();
        }

        var client = factory.CreateDefaultClient(new RedirectHandler(7), new CookieContainerHandler());
        var token = ExtractToken(await client.GetStringAsync("/Account/Login"));
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["UserName"] = "admin",
                ["Password"] = Password,
                ["__RequestVerificationToken"] = token,
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static string ExtractToken(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Antiforgery token nenalezen.");
        return match.Groups[1].Value;
    }

    [Fact]
    public async Task StartingEmployeeImport_DoesNotFailOnDisposedHttpContext()
    {
        var client = await LoginAsAdminAsync();

        var page = await client.GetStringAsync("/Catalog/Employees");
        var response = await client.PostAsync("/Catalog/Employees?handler=Sync", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = ExtractToken(page) }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Počkáme, až úloha doběhne (zdroj zaměstnanců není nastaven → skončí chybou).
        var runner = factory.Services.GetRequiredService<SyncJobRunner>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (runner.IsRunning("Import zaměstnanců") && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        var status = runner.Get("Import zaměstnanců");
        Assert.NotNull(status);
        Assert.False(status.Running);

        // Smí selhat na nenakonfigurovaném zdroji, ale NIKDY na uvolněném kontextu.
        Assert.DoesNotContain("disposed", status.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IFeatureCollection", status.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Zdroj zaměstnanců není nakonfigurován", status.Error ?? "");
    }
}
