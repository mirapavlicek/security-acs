using System.Net;
using System.Text;
using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.WinPak;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Acs.Tests;

public sealed class CardAdminTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly CardAdminService _cardAdmin;
    private readonly List<(HttpMethod Method, string Path)> _connectorCalls = [];

    private readonly Employee _employee;
    private readonly Reader _reader;

    public CardAdminTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        var settings = new SettingsService(_db, new EphemeralDataProtectionProvider());
        settings.SetAsync(SettingKeys.WinPakBaseUrl, "http://connector").GetAwaiter().GetResult();
        settings.SetAsync(SettingKeys.WinPakApiKey, "k").GetAwaiter().GetResult();

        var http = new HttpClient(new RecordingHandler(_connectorCalls));
        _cardAdmin = new CardAdminService(_db, new WinPakClient(http, settings), new AuditService(_db));

        _employee = new Employee { FirstName = "Jan", LastName = "Novák", WinPakCardHolderId = "CH-1001" };
        _reader = new Reader { Name = "Serverovna", IsActive = true, AccessLevelExternalId = "AL-03" };
        _db.Employees.Add(_employee);
        _db.Readers.Add(_reader);
        _db.Users.Add(new AppUser { UserName = "zadatel" });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class RecordingHandler(List<(HttpMethod, string)> calls) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            calls.Add((request.Method, request.RequestUri!.AbsolutePath));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)
            {
                Content = new StringContent("", Encoding.UTF8, "application/json"),
            });
        }
    }

    private AccessRequestItem CreateApprovedItem(RequestKind kind = RequestKind.Grant)
    {
        var request = new AccessRequest
        {
            Kind = kind,
            RequesterUserId = _db.Users.First().Id,
            TargetEmployeeId = _employee.Id,
            Items = [new AccessRequestItem { ReaderId = _reader.Id, Status = RequestStatus.Approved }],
        };
        _db.AccessRequests.Add(request);
        _db.SaveChanges();
        return request.Items[0];
    }

    [Fact]
    public async Task Push_Grant_CallsConnector_AndMarksPushed()
    {
        var item = CreateApprovedItem();

        await _cardAdmin.PushAsync(item.Id, "spravce");

        var (method, path) = Assert.Single(_connectorCalls);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("/api/v1/cardholders/CH-1001/access-levels", path);

        await _db.Entry(item).ReloadAsync();
        Assert.Equal(RequestStatus.PushedToWinPak, item.Status);
        Assert.NotNull(item.PushedAt);
    }

    [Fact]
    public async Task Push_Revoke_CallsDelete_AndRevokesOriginalGrant()
    {
        // Původní udělený přístup.
        var grant = CreateApprovedItem();
        grant.Status = RequestStatus.PushedToWinPak;
        await _db.SaveChangesAsync();

        var revoke = CreateApprovedItem(RequestKind.Revoke);
        await _cardAdmin.PushAsync(revoke.Id, "spravce");

        var (method, path) = Assert.Single(_connectorCalls);
        Assert.Equal(HttpMethod.Delete, method);
        Assert.Equal("/api/v1/cardholders/CH-1001/access-levels/AL-03", path);

        await _db.Entry(revoke).ReloadAsync();
        await _db.Entry(grant).ReloadAsync();
        Assert.Equal(RequestStatus.Revoked, revoke.Status);
        Assert.Equal(RequestStatus.Revoked, grant.Status);
    }

    [Fact]
    public async Task ConfirmManual_MarksConfirmed_WithoutConnectorCall()
    {
        var item = CreateApprovedItem();

        await _cardAdmin.ConfirmManualAsync(item.Id, "spravce");

        Assert.Empty(_connectorCalls);
        await _db.Entry(item).ReloadAsync();
        Assert.Equal(RequestStatus.ManuallyConfirmed, item.Status);
    }

    [Fact]
    public async Task Push_WithoutCardHolderId_Throws()
    {
        _employee.WinPakCardHolderId = null;
        await _db.SaveChangesAsync();
        var item = CreateApprovedItem();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _cardAdmin.PushAsync(item.Id, "spravce"));
        Assert.Empty(_connectorCalls);
    }
}
