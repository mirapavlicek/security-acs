using System.Net;
using System.Text;
using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Automation;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Notifications;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.Sync;
using Acs.Infrastructure.WinPak;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acs.Tests;

/// <summary>Automatizace: offboarding, změna oddělení, expirace, připomínky, auto-push.</summary>
public sealed class AutomationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly SettingsService _settings;
    private readonly RecordingNotifier _notifier = new();
    private readonly List<string> _connectorCalls = [];

    private readonly AppUser _requester;
    private readonly Employee _employee;
    private readonly Reader _reader;

    public AutomationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _settings = new SettingsService(_db, new EphemeralDataProtectionProvider());
        _settings.SetAsync(SettingKeys.WinPakBaseUrl, "http://connector").GetAwaiter().GetResult();

        _requester = new AppUser { UserName = "zadatel" };
        _employee = new Employee
        {
            FirstName = "Jan", LastName = "Novák", Department = "Chirurgie",
            WinPakCardHolderId = "CH-1", IsActive = true,
        };
        _reader = new Reader { Name = "Sál", IsActive = true, AccessLevelExternalId = "AL-1" };
        _db.AddRange(_requester, _employee, _reader);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private AutomationService CreateService()
    {
        var http = new HttpClient(new StubHandler(request =>
        {
            _connectorCalls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return (HttpStatusCode.NoContent, "");
        }));
        var cardAdmin = new CardAdminService(_db, new WinPakClient(http, _settings), new AuditService(_db));
        return new AutomationService(_db, _settings, new AuditService(_db), cardAdmin,
            NullLogger<AutomationService>.Instance, _notifier);
    }

    private AccessRequestItem AddGrant(RequestStatus status, string? autoDepartment = null,
        DateTime? validUntil = null, DateTime? createdAt = null)
    {
        var request = new AccessRequest
        {
            Kind = RequestKind.Grant,
            RequesterUserId = _requester.Id,
            TargetEmployeeId = _employee.Id,
            AutoAssignedDepartment = autoDepartment,
            ValidUntil = validUntil,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            Items = [new AccessRequestItem { ReaderId = _reader.Id, Status = status }],
        };
        _db.AccessRequests.Add(request);
        _db.SaveChanges();
        return request.Items[0];
    }

    [Fact]
    public async Task Offboarding_RevokesActiveAccess_OfInactiveEmployee()
    {
        var grant = AddGrant(RequestStatus.PushedToWinPak);
        _employee.IsActive = false;
        await _db.SaveChangesAsync();

        var result = await CreateService().RunAsync("test");

        Assert.Equal(1, result.OffboardedAccesses);
        var revoke = await _db.AccessRequests.Include(r => r.Items)
            .SingleAsync(r => r.Kind == RequestKind.Revoke);
        Assert.Equal(RequestStatus.Approved, revoke.Items[0].Status); // předschváleno → fronta karet
        Assert.Equal(_reader.Id, revoke.Items[0].ReaderId);
        _ = grant;
    }

    [Fact]
    public async Task Offboarding_CancelsNotYetPushedRequests()
    {
        var pending = AddGrant(RequestStatus.Pending);
        _employee.IsActive = false;
        await _db.SaveChangesAsync();

        var result = await CreateService().RunAsync("test");

        Assert.Equal(1, result.OffboardedAccesses);
        await _db.Entry(pending).ReloadAsync();
        Assert.Equal(RequestStatus.Cancelled, pending.Status); // není co odebírat ve WIN-PAK
        Assert.False(await _db.AccessRequests.AnyAsync(r => r.Kind == RequestKind.Revoke));
    }

    [Fact]
    public async Task Offboarding_IsIdempotent()
    {
        AddGrant(RequestStatus.PushedToWinPak);
        _employee.IsActive = false;
        await _db.SaveChangesAsync();

        Assert.Equal(1, (await CreateService().RunAsync("test")).OffboardedAccesses);
        Assert.Equal(0, (await CreateService().RunAsync("test")).OffboardedAccesses);
    }

    [Fact]
    public async Task DepartmentChange_RevokesAccessFromOldDepartment()
    {
        AddGrant(RequestStatus.PushedToWinPak, autoDepartment: "Chirurgie");
        _employee.Department = "Interna"; // přesun na jiné oddělení
        await _db.SaveChangesAsync();

        var result = await CreateService().RunAsync("test");

        Assert.Equal(1, result.DepartmentChangeRevocations);
        Assert.True(await _db.AccessRequests.AnyAsync(r => r.Kind == RequestKind.Revoke));
    }

    [Fact]
    public async Task DepartmentChange_KeepsAccessWhenDepartmentMatches()
    {
        AddGrant(RequestStatus.PushedToWinPak, autoDepartment: "chirurgie"); // jiná velikost písmen

        var result = await CreateService().RunAsync("test");

        Assert.Equal(0, result.DepartmentChangeRevocations);
    }

    [Fact]
    public async Task Expiration_RevokesAccessAfterValidUntil()
    {
        AddGrant(RequestStatus.ManuallyConfirmed, validUntil: DateTime.UtcNow.AddDays(-1));

        var result = await CreateService().RunAsync("test");

        Assert.Equal(1, result.ExpiredAccesses);
    }

    [Fact]
    public async Task Expiration_KeepsValidAccess()
    {
        AddGrant(RequestStatus.ManuallyConfirmed, validUntil: DateTime.UtcNow.AddDays(30));

        var result = await CreateService().RunAsync("test");

        Assert.Equal(0, result.ExpiredAccesses);
    }

    [Fact]
    public async Task Reminders_AreSentAfterThreshold_AndEscalated()
    {
        await _settings.SetAsync(SettingKeys.AutoReminderAfterDays, "3");
        await _settings.SetAsync(SettingKeys.AutoEscalationAfterDays, "7");
        var item = AddGrant(RequestStatus.Pending, createdAt: DateTime.UtcNow.AddDays(-10));

        var result = await CreateService().RunAsync("test");

        Assert.Equal(1, result.Reminders);
        Assert.Equal(1, result.Escalations);
        Assert.Contains(item.Id, _notifier.PendingNotified);
        Assert.Contains(item.Id, _notifier.Escalated);

        // Opakované spuštění hned poté už nepřipomíná (proti spamu).
        _notifier.Reset();
        Assert.Equal(0, (await CreateService().RunAsync("test")).Reminders);
    }

    [Fact]
    public async Task Reminders_NotSentForFreshRequests()
    {
        AddGrant(RequestStatus.Pending, createdAt: DateTime.UtcNow.AddHours(-2));
        Assert.Equal(0, (await CreateService().RunAsync("test")).Reminders);
    }

    [Fact]
    public async Task AutoPush_PushesApprovedItems_WhenEnabled()
    {
        await _settings.SetAsync(SettingKeys.AutoPushEnabled, "true");
        var approved = AddGrant(RequestStatus.Approved);

        var result = await CreateService().RunAsync("test");

        Assert.Equal(1, result.AutoPushed);
        await _db.Entry(approved).ReloadAsync();
        Assert.Equal(RequestStatus.PushedToWinPak, approved.Status);
        Assert.Single(_connectorCalls);
    }

    [Fact]
    public async Task AutoPush_IsSkippedWhenDisabled()
    {
        AddGrant(RequestStatus.Approved);
        var result = await CreateService().RunAsync("test");
        Assert.Equal(0, result.AutoPushed);
        Assert.Empty(_connectorCalls);
    }

    [Fact]
    public async Task AutoPush_FailureLeavesItemInQueue()
    {
        await _settings.SetAsync(SettingKeys.AutoPushEnabled, "true");
        _employee.WinPakCardHolderId = null; // chybí mapování → push selže
        await _db.SaveChangesAsync();
        var approved = AddGrant(RequestStatus.Approved);

        var result = await CreateService().RunAsync("test");

        Assert.Equal(0, result.AutoPushed);
        Assert.Equal(1, result.AutoPushFailed);
        await _db.Entry(approved).ReloadAsync();
        Assert.Equal(RequestStatus.Approved, approved.Status); // zůstává správci karet
    }

    [Fact]
    public async Task DisabledAutomations_DoNothing()
    {
        await _settings.SetAsync(SettingKeys.AutoOffboardingEnabled, "false");
        AddGrant(RequestStatus.PushedToWinPak);
        _employee.IsActive = false;
        await _db.SaveChangesAsync();

        Assert.Equal(0, (await CreateService().RunAsync("test")).OffboardedAccesses);
    }

    [Fact]
    public async Task HealthCheck_ReportsMissingConfiguration()
    {
        var http = new HttpClient(new StubHandler(_ => (HttpStatusCode.OK,
            """{"version":"1","providerMode":"Mssql","supportsWrite":false}""")));
        var health = new HealthCheckService(_db, _settings, new WinPakClient(http, _settings),
            new Acs.Infrastructure.Auth.DcLocator(_settings, NullLogger<Acs.Infrastructure.Auth.DcLocator>.Instance,
                srvQuery: (_, _) => Task.FromResult<IReadOnlyList<Acs.Infrastructure.Auth.DcCandidate>>([]),
                probe: (_, _, _) => Task.FromResult(true)));

        var items = await health.RunAsync();

        Assert.Contains(items, i => i.Title.Contains("jen pro čtení"));
        Assert.Contains(items, i => i.Title.Contains("Zdroj zaměstnanců"));
        Assert.Contains(items, i => i.Title.Contains("SMTP"));
    }

    private sealed class RecordingNotifier : INotificationService
    {
        public List<int> PendingNotified { get; } = [];
        public List<int> Escalated { get; } = [];

        public void Reset() { PendingNotified.Clear(); Escalated.Clear(); }

        public Task NotifyPendingAsync(int itemId, CancellationToken ct = default)
        {
            PendingNotified.Add(itemId);
            return Task.CompletedTask;
        }

        public Task NotifyDecidedAsync(int itemId, CancellationToken ct = default) => Task.CompletedTask;

        public Task NotifyEscalationAsync(int itemId, int waitingDays, CancellationToken ct = default)
        {
            Escalated.Add(itemId);
            return Task.CompletedTask;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (status, body) = responder(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
