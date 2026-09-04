using System.Net;
using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Automation;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.WinPak;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acs.Tests;

/// <summary>Fronta správce parkování: vydání (SPZ → identifikátory), odebrání, expirace a offboarding.</summary>
public sealed class ParkingAdminTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly RequestWorkflowService _workflow;
    private readonly ParkingAdminService _parking;
    private readonly SettingsService _settings;

    private readonly AppUser _admin;
    private readonly Employee _employee;
    private readonly Site _motol;
    private readonly ParkingPermitType _type;

    public ParkingAdminTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _settings = new SettingsService(_db, new EphemeralDataProtectionProvider());
        _workflow = new RequestWorkflowService(_db, new AuditService(_db));
        _parking = new ParkingAdminService(_db, new AuditService(_db));

        _admin = new AppUser { UserName = "admin", Roles = AppRole.Admin };
        _employee = new Employee { FirstName = "Jan", LastName = "Novák", IsActive = true };
        _motol = new Site { Name = "Motol" };
        _type = new ParkingPermitType { Name = "Zaměstnanec", Binding = PermitBinding.LicensePlate, MaxPlates = 2 };
        _db.AddRange(_admin, _employee, _motol, _type);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    /// <summary>Založí žádost bez matice a schválí ji administrátorem → položka ve frontě parkování.</summary>
    private async Task<AccessRequestItem> ApprovedItemAsync(params string[] plates)
    {
        var request = await _workflow.CreateParkingRequestAsync(_admin.Id, _employee.Id,
            new ParkingRequestInput(_type.Id, false, [_motol.Id], plates, null, null, DateTime.UtcNow.AddMonths(6), "test"),
            requesterCanActForOthers: true);
        var item = request.Items[0];
        await _workflow.DecideAsync(item.Id, _admin.Id, true, null, isAdmin: true);
        return item;
    }

    private async Task<AccessRequestItem> IssuedItemAsync(params string[] plates)
    {
        var item = await ApprovedItemAsync(plates);
        await _parking.IssueAsync(item.Id, _admin.Id, null, "admin");
        return item;
    }

    [Fact]
    public async Task Queue_ContainsOnlyParkingItems_CardQueueExcludesThem()
    {
        var item = await ApprovedItemAsync("1AB2345");
        var reader = new Reader { Name = "Sál", IsActive = true, AccessLevelExternalId = "AL-1" };
        _db.AccessRequests.Add(new AccessRequest
        {
            RequesterUserId = _admin.Id, TargetEmployeeId = _employee.Id,
            Items = [new AccessRequestItem { Reader = reader, Status = RequestStatus.Approved }],
        });
        await _db.SaveChangesAsync();

        var queue = await _parking.GetQueueAsync();
        Assert.Single(queue);
        Assert.Equal(item.Id, queue[0].Id);

        var http = new HttpClient(new StubHandler(_ => (HttpStatusCode.NoContent, "")));
        var cardAdmin = new CardAdminService(_db, new WinPakClient(http, _settings), new AuditService(_db));
        var cardQueue = await cardAdmin.GetQueueAsync();
        Assert.Single(cardQueue);
        Assert.Null(cardQueue[0].ParkingPermitId);
    }

    [Fact]
    public async Task Issue_AssignsNumber_CreatesPlateIdentifiers_AndMarksIssued()
    {
        var item = await ApprovedItemAsync("1AB2345", "2CD3456");

        await _parking.IssueAsync(item.Id, _admin.Id, null, "admin");

        var issued = await _parking.GetItemAsync(item.Id);
        Assert.Equal(RequestStatus.Issued, issued!.Status);
        Assert.Equal($"P-{DateTime.UtcNow.Year}-0001", issued.ParkingPermit!.PermitNumber);
        Assert.NotNull(issued.ParkingPermit.IssuedAt);
        Assert.Equal(_admin.Id, issued.ParkingPermit.IssuedByUserId);

        var identifiers = await _db.EmployeeIdentifiers
            .Where(x => x.EmployeeId == _employee.Id && x.Type == IdentifierType.LicensePlate)
            .OrderBy(x => x.Value).ToListAsync();
        Assert.Equal(["1AB2345", "2CD3456"], identifiers.Select(x => x.Value).ToArray());
        Assert.All(identifiers, x => Assert.True(x.IsActive));
        Assert.All(identifiers, x => Assert.Equal(issued.ParkingPermit.ValidTo, x.ValidTo));
        Assert.All(issued.ParkingPermit.Plates, p => Assert.NotNull(p.EmployeeIdentifierId));
    }

    [Fact]
    public async Task Issue_ReusesExistingPlateIdentifier()
    {
        _db.EmployeeIdentifiers.Add(new EmployeeIdentifier
        {
            EmployeeId = _employee.Id, Type = IdentifierType.LicensePlate, Value = "1AB2345", IsActive = false,
        });
        await _db.SaveChangesAsync();

        var item = await ApprovedItemAsync("1AB2345");
        await _parking.IssueAsync(item.Id, _admin.Id, "VIP-1", "admin");

        var identifiers = await _db.EmployeeIdentifiers.Where(x => x.Value == "1AB2345").ToListAsync();
        Assert.Single(identifiers);
        Assert.True(identifiers[0].IsActive);
        Assert.Equal("VIP-1", (await _db.ParkingPermits.FirstAsync()).PermitNumber);
    }

    [Fact]
    public async Task Issue_SequenceIncrements_AndRefusesDuplicateNumber()
    {
        var first = await IssuedItemAsync("1AB2345");
        var other = new Employee { FirstName = "Eva", LastName = "Malá", IsActive = true };
        _db.Employees.Add(other);
        await _db.SaveChangesAsync();

        var request = await _workflow.CreateParkingRequestAsync(_admin.Id, other.Id,
            new ParkingRequestInput(_type.Id, true, null, ["5XY6789"], null, null, null, null),
            requesterCanActForOthers: true);
        await _workflow.DecideAsync(request.Items[0].Id, _admin.Id, true, null, isAdmin: true);

        var firstNumber = (await _parking.GetItemAsync(first.Id))!.ParkingPermit!.PermitNumber;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _parking.IssueAsync(request.Items[0].Id, _admin.Id, firstNumber, "admin"));

        await _parking.IssueAsync(request.Items[0].Id, _admin.Id, null, "admin");
        Assert.Equal($"P-{DateTime.UtcNow.Year}-0002",
            (await _parking.GetItemAsync(request.Items[0].Id))!.ParkingPermit!.PermitNumber);
    }

    [Fact]
    public async Task Issue_RefusesNonApprovedItem()
    {
        var item = await IssuedItemAsync("1AB2345");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _parking.IssueAsync(item.Id, _admin.Id, null, "admin"));
    }

    [Fact]
    public async Task RevokeRequest_GoesStraightToQueue_AndConfirmRevokesPermit()
    {
        var grant = await IssuedItemAsync("1AB2345");
        var permitId = grant.ParkingPermitId!.Value;

        var revoke = await _workflow.CreateParkingRevokeRequestAsync(_admin.Id, permitId, "auto prodáno", requesterCanActForOthers: true);
        Assert.Equal(RequestKind.Revoke, revoke.Kind);
        Assert.Equal(RequestStatus.Approved, revoke.Items[0].Status);
        Assert.Single(await _parking.GetQueueAsync());

        // Druhá žádost o odebrání téhož povolení se odmítne.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _workflow.CreateParkingRevokeRequestAsync(_admin.Id, permitId, null, requesterCanActForOthers: true));

        await _parking.ConfirmRevokeAsync(revoke.Items[0].Id, null, "admin");

        Assert.Equal(RequestStatus.Revoked, (await _db.AccessRequestItems.FindAsync(grant.Id))!.Status);
        Assert.Equal(RequestStatus.Revoked, (await _db.AccessRequestItems.FindAsync(revoke.Items[0].Id))!.Status);
        var permit = await _db.ParkingPermits.FindAsync(permitId);
        Assert.NotNull(permit!.RevokedAt);
        var identifier = await _db.EmployeeIdentifiers.FirstAsync(x => x.Value == "1AB2345");
        Assert.False(identifier.IsActive);
        Assert.Empty(await _parking.GetIssuedAsync());
    }

    [Fact]
    public async Task DirectRevoke_CreatesAuditRequest_AndDeactivatesPlates()
    {
        var grant = await IssuedItemAsync("1AB2345");
        var permitId = grant.ParkingPermitId!.Value;

        await _parking.RevokeAsync(permitId, _admin.Id, "porušení pravidel", "admin");

        Assert.Equal(RequestStatus.Revoked, (await _db.AccessRequestItems.FindAsync(grant.Id))!.Status);
        var revokeRequest = await _db.AccessRequests.Include(r => r.Items)
            .SingleAsync(r => r.Kind == RequestKind.Revoke);
        Assert.Equal("porušení pravidel", revokeRequest.Justification);
        Assert.Equal(RequestStatus.Revoked, revokeRequest.Items[0].Status);
        Assert.False((await _db.EmployeeIdentifiers.FirstAsync(x => x.Value == "1AB2345")).IsActive);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _parking.RevokeAsync(permitId, _admin.Id, "znovu", "admin"));
    }

    [Fact]
    public async Task AfterRevoke_NewRequestForSameType_IsAllowed()
    {
        var grant = await IssuedItemAsync("1AB2345");
        await _parking.RevokeAsync(grant.ParkingPermitId!.Value, _admin.Id, "konec", "admin");

        var again = await ApprovedItemAsync("7GH8901");
        Assert.Equal(RequestStatus.Approved, again.Status);
    }

    [Fact]
    public async Task GetIssued_SearchMatchesPlateNameAndNumber()
    {
        await IssuedItemAsync("1AB2345");

        Assert.Single(await _parking.GetIssuedAsync("1ab 2345"));
        Assert.Single(await _parking.GetIssuedAsync("Novák"));
        Assert.Single(await _parking.GetIssuedAsync($"P-{DateTime.UtcNow.Year}"));
        Assert.Empty(await _parking.GetIssuedAsync("9ZZ9999"));
    }

    private AutomationService CreateAutomation()
    {
        var http = new HttpClient(new StubHandler(_ => (HttpStatusCode.NoContent, "")));
        var cardAdmin = new CardAdminService(_db, new WinPakClient(http, _settings), new AuditService(_db));
        return new AutomationService(_db, _settings, new AuditService(_db), cardAdmin,
            NullLogger<AutomationService>.Instance, parkingAdmin: _parking);
    }

    [Fact]
    public async Task Automation_ExpiredPermit_IsRevoked()
    {
        var grant = await IssuedItemAsync("1AB2345");
        var permit = await _db.ParkingPermits.FindAsync(grant.ParkingPermitId);
        permit!.ValidTo = DateTime.UtcNow.AddDays(-1);
        await _db.SaveChangesAsync();

        var result = await CreateAutomation().RunAsync("test");

        Assert.Equal(1, result.ExpiredAccesses);
        Assert.Equal(RequestStatus.Revoked, (await _db.AccessRequestItems.FindAsync(grant.Id))!.Status);
        Assert.False((await _db.EmployeeIdentifiers.FirstAsync(x => x.Value == "1AB2345")).IsActive);
    }

    [Fact]
    public async Task Automation_InactiveEmployee_LosesPermit()
    {
        var grant = await IssuedItemAsync("1AB2345");
        _employee.IsActive = false;
        await _db.SaveChangesAsync();

        var result = await CreateAutomation().RunAsync("test");

        Assert.Equal(1, result.OffboardedAccesses);
        Assert.Equal(RequestStatus.Revoked, (await _db.AccessRequestItems.FindAsync(grant.Id))!.Status);
    }

    [Fact]
    public async Task Automation_PendingParkingRequest_OfInactiveEmployee_IsCancelled()
    {
        var request = await _workflow.CreateParkingRequestAsync(_admin.Id, _employee.Id,
            new ParkingRequestInput(_type.Id, false, [_motol.Id], ["1AB2345"], null, null, null, null),
            requesterCanActForOthers: true);
        _employee.IsActive = false;
        await _db.SaveChangesAsync();

        await CreateAutomation().RunAsync("test");

        Assert.Equal(RequestStatus.Cancelled, (await _db.AccessRequestItems.FindAsync(request.Items[0].Id))!.Status);
        Assert.Empty(await _db.AccessRequests.Where(r => r.Kind == RequestKind.Revoke).ToListAsync());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (status, body) = responder(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
