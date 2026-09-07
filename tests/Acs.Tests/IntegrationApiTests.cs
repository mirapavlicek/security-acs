using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acs.Tests;

/// <summary>
/// Integrační API pro parkovací systém přes celou aplikaci: fail-closed bez zapnutí a klíče,
/// ověření X-Api-Key, online autorizace a hlášení událostí; „Moje přístupy“ se SPZ a vjezdy.
/// </summary>
public class IntegrationApiTests(AcsWebFactory factory) : IClassFixture<AcsWebFactory>
{
    private const string ApiKey = "gc-test-key-123";
    private const string Base = "/api/integration/v1";

    private async Task ConfigureAsync(bool enabled, string? apiKey, string? allowedIps = null)
    {
        using var scope = factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        await settings.SetAsync(SettingKeys.ParkingSystemEnabled, enabled ? "true" : "false");
        await settings.SetAsync(SettingKeys.ParkingSystemApiKey, apiKey);
        await settings.SetAsync(SettingKeys.ParkingSystemAllowedIps, allowedIps);
    }

    /// <summary>Založí zaměstnance s vydaným povolením na SPZ pro areál s vjezdem <c>GATE-T</c>.</summary>
    private async Task<(int EmployeeId, string Plate)> SeedIssuedPermitAsync(string plate, string adAccount)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AcsDbContext>();
        var workflow = scope.ServiceProvider.GetRequiredService<RequestWorkflowService>();
        var parking = scope.ServiceProvider.GetRequiredService<ParkingAdminService>();

        var admin = await db.Users.FirstAsync(u => u.UserName == "admin");
        var site = await db.Sites.FirstOrDefaultAsync(s => s.Code == "TST")
                   ?? db.Sites.Add(new Site { Name = "Testovací areál", Code = "TST", GateExternalIds = "GATE-T" }).Entity;
        var type = await db.ParkingPermitTypes.FirstOrDefaultAsync(t => t.Code == "TST")
                   ?? db.ParkingPermitTypes.Add(new ParkingPermitType { Name = "Test", Code = "TST", Binding = PermitBinding.LicensePlate }).Entity;
        var employee = new Employee { FirstName = "Eva", LastName = "Vjezdová", IsActive = true, AdAccount = adAccount };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var request = await workflow.CreateParkingRequestAsync(admin.Id, employee.Id,
            new ParkingRequestInput(type.Id, false, [site.Id], [plate], null, null, DateTime.UtcNow.AddMonths(3), "test"),
            requesterCanActForOthers: true);
        await workflow.DecideAsync(request.Items[0].Id, admin.Id, true, null, isAdmin: true);
        await parking.IssueAsync(request.Items[0].Id, admin.Id, null, "admin");
        return (employee.Id, plate);
    }

    private HttpClient ApiClient(string? key = ApiKey)
    {
        var client = factory.CreateClient();
        if (key is not null)
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    [Fact]
    public async Task Api_IsClosed_WhenDisabledOrWithoutKey()
    {
        await ConfigureAsync(enabled: false, apiKey: ApiKey);
        var disabled = await ApiClient().GetAsync(Base);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
        Assert.Equal("application/problem+json", disabled.Content.Headers.ContentType?.MediaType);

        await ConfigureAsync(enabled: true, apiKey: null);
        var noKey = await ApiClient().GetAsync(Base);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, noKey.StatusCode);

        await ConfigureAsync(enabled: true, apiKey: ApiKey);
        var wrongKey = await ApiClient("jiny-klic").GetAsync(Base);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongKey.StatusCode);
        var missingKey = await ApiClient(key: null).GetAsync(Base);
        Assert.Equal(HttpStatusCode.Unauthorized, missingKey.StatusCode);

        var ok = await ApiClient().GetAsync(Base);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("checkAuthorization", await ok.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Api_RejectsCallsFromOtherIps_WhenAllowlistSet()
    {
        await ConfigureAsync(enabled: true, apiKey: ApiKey, allowedIps: "10.99.0.0/16");
        try
        {
            var response = await ApiClient().GetAsync(Base);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            await ConfigureAsync(enabled: true, apiKey: ApiKey);
        }
    }

    [Fact]
    public async Task AuthorizationCheck_AllowsIssuedPlate_AndDeniesUnknown()
    {
        await ConfigureAsync(enabled: true, apiKey: ApiKey);
        var (employeeId, plate) = await SeedIssuedPermitAsync("5TE1234", "eva.api");
        var client = ApiClient();

        var allow = await client.PostAsJsonAsync($"{Base}/authorization-checks", new
        {
            accessPointId = "GATE-T",
            credential = new { type = "licensePlate", value = "5TE 1234" },
            direction = "in",
            confidence = 0.97,
        });
        Assert.Equal(HttpStatusCode.OK, allow.StatusCode);
        using var allowDoc = JsonDocument.Parse(await allow.Content.ReadAsStringAsync());
        Assert.Equal("allow", allowDoc.RootElement.GetProperty("decision").GetString());
        Assert.Equal("allowed", allowDoc.RootElement.GetProperty("reason").GetString());
        Assert.Equal(employeeId.ToString(), allowDoc.RootElement.GetProperty("personId").GetString());
        Assert.Equal("Eva Vjezdová", allowDoc.RootElement.GetProperty("personName").GetString());
        Assert.EndsWith("Z", allowDoc.RootElement.GetProperty("decidedAt").GetString());

        var deny = await client.PostAsJsonAsync($"{Base}/authorization-checks", new
        {
            accessPointId = "GATE-T",
            credential = new { type = "licensePlate", value = "0NE0000" },
        });
        Assert.Equal(HttpStatusCode.OK, deny.StatusCode); // zamítnutí není chyba volání
        using var denyDoc = JsonDocument.Parse(await deny.Content.ReadAsStringAsync());
        Assert.Equal("deny", denyDoc.RootElement.GetProperty("decision").GetString());
        Assert.Equal("credentialUnknown", denyDoc.RootElement.GetProperty("reason").GetString());

        var bad = await client.PostAsJsonAsync($"{Base}/authorization-checks", new { credential = new { type = "licensePlate", value = "X" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var badType = await client.PostAsJsonAsync($"{Base}/authorization-checks", new
        {
            accessPointId = "GATE-T", credential = new { type = "retina", value = "X" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);
    }

    [Fact]
    public async Task Events_AreAccepted_AndReadEndpointsReturnPermitState()
    {
        await ConfigureAsync(enabled: true, apiKey: ApiKey);
        var (employeeId, plate) = await SeedIssuedPermitAsync("6EV5678", "eva.events");
        var client = ApiClient();

        var report = await client.PostAsJsonAsync($"{Base}/events", new
        {
            events = new object[]
            {
                new { id = $"e-{employeeId}-1", type = "vehicleIn", occurredAt = DateTime.UtcNow, accessPointId = "GATE-T", credential = new { type = "licensePlate", value = plate } },
                new { id = $"e-{employeeId}-1", type = "vehicleIn", occurredAt = DateTime.UtcNow, accessPointId = "GATE-T" },
            },
        });
        Assert.Equal(HttpStatusCode.Accepted, report.StatusCode);
        using var reportDoc = JsonDocument.Parse(await report.Content.ReadAsStringAsync());
        Assert.Equal(1, reportDoc.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal(1, reportDoc.RootElement.GetProperty("duplicates").GetInt32());

        var empty = await client.PostAsJsonAsync($"{Base}/events", new { events = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var person = await client.GetAsync($"{Base}/persons/{employeeId}");
        Assert.Equal(HttpStatusCode.OK, person.StatusCode);
        Assert.Contains("Vjezdová", await person.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Base}/persons/999999")).StatusCode);

        var credentials = await client.GetStringAsync($"{Base}/persons/{employeeId}/credentials");
        Assert.Contains(plate, credentials);
        Assert.Contains("licensePlate", credentials);

        var entitlements = await client.GetStringAsync($"{Base}/entitlements?personId={employeeId}");
        Assert.Contains("site:TST", entitlements);
        Assert.Contains("\"status\":\"active\"", entitlements);

        var accessPoints = await client.GetStringAsync($"{Base}/access-points");
        Assert.Contains("GATE-T", accessPoints);
    }

    [Fact]
    public async Task MyAccess_ShowsPlatesParkingPermitsAndGateEvents()
    {
        await ConfigureAsync(enabled: true, apiKey: ApiKey);
        var (employeeId, plate) = await SeedIssuedPermitAsync("7MA9012", "eva.myaccess");
        await ApiClient().PostAsJsonAsync($"{Base}/authorization-checks", new
        {
            accessPointId = "GATE-T", credential = new { type = "licensePlate", value = plate },
        });

        const string password = "Moje!Pristupy1";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AcsDbContext>();
            db.Users.Add(new AppUser
            {
                UserName = "eva.myaccess", DisplayName = "Eva Vjezdová", IsLocal = true, Roles = AppRole.Employee,
                PasswordHash = Acs.Infrastructure.Auth.PasswordHasher.Hash(password), MustChangePassword = false,
                EmployeeId = employeeId,
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateDefaultClient(new RedirectHandler(7), new CookieContainerHandler());
        var loginPage = await client.GetStringAsync("/Account/Login");
        await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["UserName"] = "eva.myaccess",
            ["Password"] = password,
            ["__RequestVerificationToken"] = Regex.Match(loginPage,
                "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value,
        }));

        var html = await client.GetStringAsync("/MyAccess");
        Assert.Contains("Identifikátory", html);
        Assert.Contains(plate, html);
        Assert.Contains("Vjezdy a parkování", html);
        Assert.Contains("Testovací areál", html);
        Assert.Contains("Kartička (PDF)", html);
        Assert.Contains("Poslední průjezdy bránou", html);
        Assert.Contains("vjezd povolen", html);
    }
}
