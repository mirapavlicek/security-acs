using System.Net;
using System.Text;
using System.Text.Json;
using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.Sync;
using Acs.Infrastructure.WinPak;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Acs.Tests;

/// <summary>Zpětná synchronizace stavu z WIN-PAK do ACS.</summary>
public sealed class AccessSyncTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;

    private readonly Employee _employee;
    private readonly Reader _server;   // AL-03
    private readonly Reader _entrance; // AL-01
    private readonly AppUser _requester;

    public AccessSyncTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _employee = new Employee { FirstName = "Jan", LastName = "Novák", WinPakCardHolderId = "CH-1001" };
        _server = new Reader { Name = "Serverovna", IsActive = true, AccessLevelExternalId = "AL-03" };
        _entrance = new Reader { Name = "Hlavní vchod", IsActive = true, AccessLevelExternalId = "AL-01" };
        _requester = new AppUser { UserName = "zadatel" };
        _db.AddRange(_employee, _server, _entrance, _requester);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private AccessSyncService CreateService(params object[] cardHolders)
    {
        var settings = new SettingsService(_db, new EphemeralDataProtectionProvider());
        settings.SetAsync(SettingKeys.WinPakBaseUrl, "http://connector").GetAwaiter().GetResult();
        var http = new HttpClient(new StubJsonHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/v1/cardholders"
                ? JsonSerializer.Serialize(cardHolders)
                : "[]"));
        return new AccessSyncService(_db, new WinPakClient(http, settings), new AuditService(_db));
    }

    private sealed class StubJsonHandler(Func<HttpRequestMessage, string> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responder(request), Encoding.UTF8, "application/json"),
            });
    }

    private static object Holder(string id, string[] accessLevels, string card = "100234")
        => new
        {
            id, firstName = "Jan", lastName = "Novák", note = (string?)null,
            cards = new[] { new { cardNumber = card, status = "Active",
                activationDate = (DateTime?)null, expirationDate = (DateTime?)null } },
            accessLevelIds = accessLevels,
        };

    private AccessRequestItem AddItem(Reader reader, RequestStatus status)
    {
        var request = new AccessRequest
        {
            RequesterUserId = _requester.Id,
            TargetEmployeeId = _employee.Id,
            Items = [new AccessRequestItem { ReaderId = reader.Id, Status = status }],
        };
        _db.AccessRequests.Add(request);
        _db.SaveChanges();
        return request.Items[0];
    }

    [Fact]
    public async Task ExternalGrant_CreatesConfirmedSystemRequest()
    {
        var sync = CreateService(Holder("CH-1001", ["AL-03"]));
        var result = await sync.SyncAsync("test");

        Assert.Equal(1, result.ExternallyGranted);
        var item = await _db.AccessRequestItems.Include(i => i.Request!).ThenInclude(r => r.RequesterUser)
            .SingleAsync();
        Assert.Equal(_server.Id, item.ReaderId);
        Assert.Equal(RequestStatus.ManuallyConfirmed, item.Status);
        Assert.Equal(AccessSyncService.SystemUserName, item.Request!.RequesterUser!.UserName);
    }

    [Fact]
    public async Task ExternalRevoke_MarksActiveItemRevoked()
    {
        var item = AddItem(_server, RequestStatus.PushedToWinPak);

        // WIN-PAK už AL-03 nemá (jen vchod).
        var sync = CreateService(Holder("CH-1001", ["AL-01"]));
        var result = await sync.SyncAsync("test");

        Assert.Equal(1, result.ExternallyRevoked);
        await _db.Entry(item).ReloadAsync();
        Assert.Equal(RequestStatus.Revoked, item.Status);
        // AL-01 (vchod) se zároveň propíše jako externí udělení.
        Assert.Equal(1, result.ExternallyGranted);
    }

    [Fact]
    public async Task QueuedItem_AlreadyInWinPak_IsAutoConfirmed()
    {
        var item = AddItem(_server, RequestStatus.Approved);

        var sync = CreateService(Holder("CH-1001", ["AL-03"]));
        var result = await sync.SyncAsync("test");

        Assert.Equal(1, result.AutoConfirmed);
        Assert.Equal(0, result.ExternallyGranted); // nezaloží se duplicitní záznam
        await _db.Entry(item).ReloadAsync();
        Assert.Equal(RequestStatus.ManuallyConfirmed, item.Status);
    }

    [Fact]
    public async Task MatchingState_ChangesNothing()
    {
        AddItem(_server, RequestStatus.PushedToWinPak);

        var sync = CreateService(Holder("CH-1001", ["AL-03"]));
        var result = await sync.SyncAsync("test");

        Assert.Equal(0, result.ExternallyGranted);
        Assert.Equal(0, result.ExternallyRevoked);
        Assert.Equal(0, result.AutoConfirmed);
    }

    [Fact]
    public async Task CardNumber_IsUpdated_AndHolderMatchedByCardFallback()
    {
        _employee.WinPakCardHolderId = null;
        _employee.CardNumber = "100234";
        await _db.SaveChangesAsync();

        var sync = CreateService(Holder("CH-9", ["AL-01"], card: "999888"));
        // fallback podle karty nenajde (jiné číslo) → nic se nestane
        var result = await sync.SyncAsync("test");
        Assert.Equal(0, result.ExternallyGranted);

        sync = CreateService(Holder("CH-9", ["AL-01"], card: "100234"));
        result = await sync.SyncAsync("test");

        Assert.Equal(1, result.ExternallyGranted);
        await _db.Entry(_employee).ReloadAsync();
        Assert.Equal("CH-9", _employee.WinPakCardHolderId); // dopárováno
    }
}
