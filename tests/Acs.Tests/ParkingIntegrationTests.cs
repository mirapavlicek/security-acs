using System.Net;
using System.Text;
using System.Text.Json;
using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Integration;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Acs.Tests;

/// <summary>Zaznamenává odeslané požadavky a odpovídá podle cesty — náhrada konektoru parkovacího systému.</summary>
internal sealed class RecordingHandler(Func<HttpRequestMessage, string, (HttpStatusCode, string)> responder) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
        var (status, response) = responder(request, body);
        return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
    }
}

/// <summary>
/// Integrace parkovacího systému: online autorizace u brány podle SPZ a vydaných povolení,
/// příjem událostí, projekce oprávnění pro odběr a předání stavu konektoru (vzor A).
/// </summary>
public sealed class ParkingIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly SettingsService _settings;
    private readonly RequestWorkflowService _workflow;
    private readonly ParkingAdminService _parking;
    private readonly GateAuthorizationService _gate;

    private readonly AppUser _admin;
    private readonly Employee _employee;
    private readonly Site _motol;
    private readonly Site _homolka;
    private readonly ParkingPermitType _type;

    public ParkingIntegrationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _settings = new SettingsService(_db, new EphemeralDataProtectionProvider());
        _workflow = new RequestWorkflowService(_db, new AuditService(_db));
        _parking = new ParkingAdminService(_db, new AuditService(_db));
        _gate = new GateAuthorizationService(_db, _settings);

        _admin = new AppUser { UserName = "admin", Roles = AppRole.Admin };
        _employee = new Employee { FirstName = "Jan", LastName = "Novák", IsActive = true, PersonalNumber = "13483" };
        _motol = new Site { Name = "Motol", Code = "MOT", GateExternalIds = "GATE-N, GATE-S" };
        _homolka = new Site { Name = "Homolka", Code = "HOM", GateExternalIds = "HOM-1" };
        _type = new ParkingPermitType { Name = "Zaměstnanec", Binding = PermitBinding.LicensePlate, MaxPlates = 2 };
        _db.AddRange(_admin, _employee, _motol, _homolka, _type);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<ParkingPermit> IssuedPermitAsync(bool allSites = false, IEnumerable<int>? siteIds = null,
        DateTime? validTo = null, params string[] plates)
    {
        var request = await _workflow.CreateParkingRequestAsync(_admin.Id, _employee.Id,
            new ParkingRequestInput(_type.Id, allSites, allSites ? [] : (siteIds ?? [_motol.Id]).ToList(), plates, null, null,
                validTo ?? DateTime.UtcNow.AddMonths(6), "test"),
            requesterCanActForOthers: true);
        var item = request.Items[0];
        await _workflow.DecideAsync(item.Id, _admin.Id, true, null, isAdmin: true);
        await _parking.IssueAsync(item.Id, _admin.Id, null, "admin");
        return (await _parking.GetItemAsync(item.Id))!.ParkingPermit!;
    }

    private static AuthorizationRequest Check(string gate, string plate, string direction = "in")
        => new(gate, new CredentialRef("licensePlate", plate), direction);

    // ---------- online autorizace ----------

    [Fact]
    public async Task Allow_ForIssuedPermit_MatchesGateIdAndNormalizesPlate()
    {
        var permit = await IssuedPermitAsync(plates: ["1AB2345"]);

        var decision = await _gate.DecideAsync(Check("gate-s", "1ab 23-45"));

        Assert.True(decision.IsAllowed);
        Assert.Equal(DecisionReason.Allowed, decision.Reason);
        Assert.Equal(_employee.Id.ToString(), decision.PersonId);
        Assert.Equal("Jan Novák", decision.PersonName);
        Assert.Equal($"permit-{permit.Id}", decision.EntitlementId);
        Assert.Equal(permit.ValidTo, decision.ValidTo);
        Assert.Equal(300, decision.CacheTtlSeconds);
        Assert.False(string.IsNullOrEmpty(decision.TraceId));

        var recorded = await _db.IntegrationEvents.SingleAsync();
        Assert.Equal(GateAuthorizationService.CheckEventType, recorded.Type);
        Assert.Equal("allow", recorded.Decision);
        Assert.Equal("1AB2345", recorded.CredentialValue);
        Assert.Equal(_motol.Id, recorded.SiteId);
        Assert.Equal(_employee.Id, recorded.EmployeeId);
        Assert.Equal(decision.TraceId, recorded.TraceId);
    }

    [Fact]
    public async Task Allow_BySiteCode_WhenGateIdsNotConfigured()
    {
        _motol.GateExternalIds = null;
        await _db.SaveChangesAsync();
        await IssuedPermitAsync(plates: ["1AB2345"]);

        Assert.True((await _gate.DecideAsync(Check("MOT", "1AB2345"))).IsAllowed);
        Assert.True((await _gate.DecideAsync(Check("site:MOT", "1AB2345"))).IsAllowed);
    }

    [Fact]
    public async Task Deny_UnknownPlate_UnknownGate_AndOtherSite()
    {
        await IssuedPermitAsync(plates: ["1AB2345"]);

        var unknownPlate = await _gate.DecideAsync(Check("GATE-N", "9ZZ9999"));
        Assert.False(unknownPlate.IsAllowed);
        Assert.Equal(DecisionReason.CredentialUnknown, unknownPlate.Reason);
        Assert.Null(unknownPlate.PersonName);
        Assert.Equal(0, unknownPlate.CacheTtlSeconds);

        var unknownGate = await _gate.DecideAsync(Check("NEEXISTUJE", "1AB2345"));
        Assert.Equal(DecisionReason.AccessPointUnknown, unknownGate.Reason);

        var otherSite = await _gate.DecideAsync(Check("HOM-1", "1AB2345"));
        Assert.Equal(DecisionReason.NoEntitlement, otherSite.Reason);
        Assert.Equal(_employee.Id.ToString(), otherSite.PersonId); // osoba známá, aby šlo dohledat
    }

    [Fact]
    public async Task Allow_AllSitesPermit_AtAnyGate()
    {
        await IssuedPermitAsync(allSites: true, plates: ["1AB2345"]);

        Assert.True((await _gate.DecideAsync(Check("HOM-1", "1AB2345"))).IsAllowed);
        Assert.True((await _gate.DecideAsync(Check("GATE-N", "1AB2345"))).IsAllowed);
    }

    [Fact]
    public async Task Deny_AfterRevoke_AndWhenPermitExpired()
    {
        var permit = await IssuedPermitAsync(plates: ["1AB2345"]);
        await _parking.RevokeAsync(permit.Id, _admin.Id, "test", "admin");

        var revoked = await _gate.DecideAsync(Check("GATE-N", "1AB2345"));
        Assert.False(revoked.IsAllowed);
        Assert.Equal(DecisionReason.CredentialExpired, revoked.Reason); // vydání zapsalo SPZ s platností povolení, odebrání ji zneplatnilo

        // Prošlé povolení s identifikátorem bez omezení → důvod je u oprávnění.
        var expired = new ParkingPermit
        {
            EmployeeId = _employee.Id, PermitTypeId = _type.Id, IssuedAt = DateTime.UtcNow.AddYears(-1),
            ValidFrom = DateTime.UtcNow.AddYears(-1), ValidTo = DateTime.UtcNow.AddDays(-1), PermitNumber = "P-OLD",
            Sites = [new ParkingPermitSite { SiteId = _homolka.Id }],
            Plates = [new ParkingPermitPlate { Value = "7EX1234" }],
        };
        _db.ParkingPermits.Add(expired);
        _db.EmployeeIdentifiers.Add(new EmployeeIdentifier { EmployeeId = _employee.Id, Type = IdentifierType.LicensePlate, Value = "7EX1234" });
        await _db.SaveChangesAsync();

        var decision = await _gate.DecideAsync(Check("HOM-1", "7EX1234"));
        Assert.Equal(DecisionReason.EntitlementExpired, decision.Reason);
    }

    [Fact]
    public async Task Deny_PlateBoundPermit_DoesNotCoverOtherPlateOfSameEmployee()
    {
        await IssuedPermitAsync(plates: ["1AB2345"]);
        _db.EmployeeIdentifiers.Add(new EmployeeIdentifier { EmployeeId = _employee.Id, Type = IdentifierType.LicensePlate, Value = "2CD3456" });
        await _db.SaveChangesAsync();

        var decision = await _gate.DecideAsync(Check("GATE-N", "2CD3456"));
        Assert.Equal(DecisionReason.NoEntitlement, decision.Reason);
    }

    [Fact]
    public async Task Deny_EndedEmployee()
    {
        await IssuedPermitAsync(plates: ["1AB2345"]);
        _employee.IsActive = false;
        await _db.SaveChangesAsync();

        var decision = await _gate.DecideAsync(Check("GATE-N", "1AB2345"));
        Assert.Equal(DecisionReason.PersonEnded, decision.Reason);
    }

    [Fact]
    public async Task Exit_IsAllowedByDefault_WithOriginalReasonRecorded_AndCanBeDisabled()
    {
        var exit = await _gate.DecideAsync(Check("GATE-N", "9ZZ9999", "out"));
        Assert.True(exit.IsAllowed);
        Assert.Equal(DecisionReason.Allowed, exit.Reason);
        Assert.Null(exit.PersonName);

        var recorded = await _db.IntegrationEvents.SingleAsync();
        Assert.Equal("out", recorded.Direction);
        Assert.Contains("\"exitOverride\":\"credentialUnknown\"", recorded.Details);

        await _settings.SetAsync(SettingKeys.ParkingSystemExitAlwaysAllowed, "false");
        var strict = await _gate.DecideAsync(Check("GATE-N", "9ZZ9999", "out"));
        Assert.False(strict.IsAllowed);
        Assert.Equal(DecisionReason.CredentialUnknown, strict.Reason);
    }

    // ---------- události ----------

    [Fact]
    public async Task Events_AreStored_Deduplicated_AndValidated()
    {
        await IssuedPermitAsync(plates: ["1AB2345"]);
        var events = new IntegrationEventService(_db);
        var when = DateTime.UtcNow.AddMinutes(-1);

        var first = await events.ReportAsync("parking",
        [
            new IntegrationEventInput("gc-1", "vehicleIn", when, "GATE-N", null, new CredentialRef("licensePlate", "1AB 2345"), Confidence: 0.98),
            new IntegrationEventInput("gc-2", "denied", when, "GATE-N", null, new CredentialRef("licensePlate", "9ZZ9999"), DecisionReason: "credentialUnknown"),
            new IntegrationEventInput("gc-3", "teleport", when),
            new IntegrationEventInput("gc-4", "denied", when, "GATE-N"),
            new IntegrationEventInput("gc-1", "vehicleIn", when, "GATE-N"),
        ]);

        Assert.Equal(2, first.Accepted);
        Assert.Equal(2, first.Rejected);
        Assert.Equal(1, first.Duplicates);
        Assert.Equal(["accepted", "accepted", "rejected", "rejected", "duplicate"], first.Results.Select(r => r.Status));

        var stored = await _db.IntegrationEvents.OrderBy(e => e.ExternalId).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.Equal(_employee.Id, stored[0].EmployeeId);
        Assert.Equal(_motol.Id, stored[0].SiteId);
        Assert.Equal("1AB2345", stored[0].CredentialValue);
        Assert.Equal("in", stored[0].Direction);
        Assert.Equal(0.98, stored[0].Confidence);
        Assert.Equal("deny", stored[1].Decision);
        Assert.Null(stored[1].EmployeeId);

        var again = await events.ReportAsync("parking",
            [new IntegrationEventInput("gc-1", "vehicleIn", when, "GATE-N")]);
        Assert.Equal(0, again.Accepted);
        Assert.Equal(1, again.Duplicates);
        Assert.Equal(2, await _db.IntegrationEvents.CountAsync());
    }

    // ---------- odběr stavu (vzor B) ----------

    [Fact]
    public async Task Catalog_ProjectsPermitsToEntitlements_PersonsAndCredentials()
    {
        var permit = await IssuedPermitAsync(siteIds: [_motol.Id, _homolka.Id], plates: ["1AB2345"]);
        var other = new Employee { FirstName = "Bez", LastName = "Povolení", IsActive = true };
        _db.Employees.Add(other);
        await _db.SaveChangesAsync();
        var catalog = new IntegrationCatalogService(_db);

        var entitlements = await catalog.ListEntitlementsAsync(null, null, null, null, null);
        Assert.Equal(2, entitlements.Items.Count);
        Assert.Null(entitlements.NextCursor);
        Assert.Equal(["site:HOM", "site:MOT"], entitlements.Items.Select(e => e.Target.AccessPointId).OrderBy(x => x));
        Assert.All(entitlements.Items, e =>
        {
            Assert.Equal("active", e.Status);
            Assert.Equal(_employee.Id.ToString(), e.PersonId);
            Assert.Equal(permit.PermitNumber, e.PermitNumber);
            Assert.Single(e.Credentials);
            Assert.Equal("1AB2345", e.Credentials[0].Value);
            Assert.StartsWith("req-", e.Source.RequestId);
        });

        var persons = await catalog.ListPersonsAsync(null, null, null);
        Assert.Single(persons.Items); // zaměstnanec bez SPZ a povolení parkovací systém nezajímá
        Assert.Equal("13483", persons.Items[0].PersonalNumber);
        Assert.Equal("active", persons.Items[0].Status);

        var credentials = await catalog.GetCredentialsAsync(_employee.Id);
        Assert.NotNull(credentials);
        Assert.Single(credentials);
        Assert.Equal("licensePlate", credentials[0].Type);

        var accessPoints = await catalog.ListAccessPointsAsync();
        var motol = accessPoints.Single(a => a.Id == "site:MOT");
        Assert.Equal("gate", motol.Kind);
        Assert.Equal(["GATE-N", "GATE-S"], motol.ExternalIds.Values.OrderBy(x => x));

        await _parking.RevokeAsync(permit.Id, _admin.Id, "konec", "admin");
        var afterRevoke = await catalog.ListEntitlementsAsync(null, null, null, null, null);
        Assert.All(afterRevoke.Items, e => Assert.Equal("revoked", e.Status));
    }

    // ---------- předání konektoru (vzor A) ----------

    private async Task<(ParkingProvisioningService Service, RecordingHandler Handler)> ProvisioningAsync(
        Func<HttpRequestMessage, string, (HttpStatusCode, string)> responder)
    {
        await _settings.SetAsync(SettingKeys.ParkingSystemPushEnabled, "true");
        await _settings.SetAsync(SettingKeys.ParkingSystemConnectorBaseUrl, "http://connector.test/api/v1");
        await _settings.SetAsync(SettingKeys.ParkingSystemConnectorApiKey, "tajny");
        var handler = new RecordingHandler(responder);
        var client = new ParkingConnectorClient(new HttpClient(handler), _settings);
        return (new ParkingProvisioningService(_db, client, _settings, new AuditService(_db)), handler);
    }

    [Fact]
    public async Task Provisioning_SendsPersonCredentialsAndEntitlements_AndStoresTargetId()
    {
        var permit = await IssuedPermitAsync(plates: ["1AB2345"]);
        var (service, handler) = await ProvisioningAsync((request, _) => request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath.EndsWith($"/persons/{_employee.Id}")
            ? (HttpStatusCode.OK, """{"targetId":"GC-77","created":true}""")
            : request.RequestUri!.AbsolutePath.EndsWith("/entitlements")
                ? (HttpStatusCode.OK, """{"applied":1,"removed":0,"targetReferences":["WL-MOT"]}""")
                : (HttpStatusCode.NoContent, ""));

        var result = await service.SyncEmployeeAsync(_employee.Id, "admin");

        Assert.True(result.Success, result.Message);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Put, r.Method));
        Assert.Contains("\"personalNumber\":\"13483\"", handler.Requests[0].Body);
        Assert.Contains("\"status\":\"active\"", handler.Requests[0].Body);
        Assert.Contains("\"type\":\"licensePlate\"", handler.Requests[1].Body);
        Assert.Contains("\"value\":\"1AB2345\"", handler.Requests[1].Body);
        Assert.Contains("\"groupId\":\"site:MOT\"", handler.Requests[2].Body);
        Assert.Contains($"\"entitlementId\":\"permit-{permit.Id}\"", handler.Requests[2].Body);
        Assert.Contains("WL-MOT", result.Message);

        var employee = await _db.Employees.FindAsync(_employee.Id);
        Assert.Equal("GC-77", employee!.ParkingSystemId);
        var stored = await _db.ParkingPermits.FindAsync(permit.Id);
        Assert.NotNull(stored!.ParkingSystemSyncedAt);
        Assert.Null(stored.ParkingSystemSyncError);
    }

    [Fact]
    public async Task Provisioning_RecordsConnectorError_WithoutThrowing()
    {
        var permit = await IssuedPermitAsync(plates: ["1AB2345"]);
        var (service, _) = await ProvisioningAsync((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/credentials")
            ? (HttpStatusCode.BadGateway, """{"title":"Target unavailable","detail":"GPSW Kernel neodpovídá"}""")
            : (HttpStatusCode.OK, """{"targetId":"GC-77"}"""));

        var result = await service.SyncEmployeeAsync(_employee.Id, "admin");

        Assert.True(result.Attempted);
        Assert.False(result.Success);
        Assert.Contains("nedostupný", result.Message);
        Assert.Contains("GPSW Kernel neodpovídá", result.Message);
        var stored = await _db.ParkingPermits.FindAsync(permit.Id);
        Assert.Null(stored!.ParkingSystemSyncedAt);
        Assert.Equal(result.Message, stored.ParkingSystemSyncError);
        Assert.True(await _db.AuditLogs.AnyAsync(a => a.Action == "parking-system-sync-failed"));
    }

    [Fact]
    public async Task Provisioning_IsSkipped_WhenNotEnabled()
    {
        var handler = new RecordingHandler((_, _) => (HttpStatusCode.OK, "{}"));
        var service = new ParkingProvisioningService(_db,
            new ParkingConnectorClient(new HttpClient(handler), _settings), _settings, new AuditService(_db));

        var result = await service.SyncEmployeeAsync(_employee.Id, "admin");

        Assert.False(result.Attempted);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Issue_PushesToConnector_WhenProvisioningWired()
    {
        var (service, handler) = await ProvisioningAsync((_, _) => (HttpStatusCode.OK, """{"targetId":"GC-1","applied":1}"""));
        var parking = new ParkingAdminService(_db, new AuditService(_db), provisioning: service);

        var request = await _workflow.CreateParkingRequestAsync(_admin.Id, _employee.Id,
            new ParkingRequestInput(_type.Id, false, [_motol.Id], ["1AB2345"], null, null, DateTime.UtcNow.AddMonths(6), "test"),
            requesterCanActForOthers: true);
        var item = request.Items[0];
        await _workflow.DecideAsync(item.Id, _admin.Id, true, null, isAdmin: true);
        await parking.IssueAsync(item.Id, _admin.Id, null, "admin");

        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("site:MOT", handler.Requests[2].Body);

        handler.Requests.Clear();
        await parking.RevokeAsync(item.ParkingPermitId!.Value, _admin.Id, "test", "admin");
        Assert.Equal(3, handler.Requests.Count);
        using var doc = JsonDocument.Parse(handler.Requests[2].Body);
        Assert.Equal(0, doc.RootElement.GetProperty("entitlements").GetArrayLength()); // po odebrání se posílá prázdný stav
    }
}
