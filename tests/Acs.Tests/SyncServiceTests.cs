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

/// <summary>HTTP handler vracející předpřipravené JSON odpovědi podle cesty.</summary>
file sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> responder) : HttpMessageHandler
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

public sealed class SyncServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly SettingsService _settings;
    private readonly AuditService _audit;

    public SyncServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _settings = new SettingsService(_db, new EphemeralDataProtectionProvider());
        _audit = new AuditService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private WinPakClient CreateWinPakClient(params object[] readers)
    {
        _settings.SetAsync(SettingKeys.WinPakBaseUrl, "http://stub").GetAwaiter().GetResult();
        _settings.SetAsync(SettingKeys.WinPakApiKey, "k").GetAwaiter().GetResult();
        var http = new HttpClient(new StubHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/readers")
                ? (HttpStatusCode.OK, JsonSerializer.Serialize(readers))
                : (HttpStatusCode.NotFound, "{}")));
        return new WinPakClient(http, _settings);
    }

    [Fact]
    public async Task ReaderSync_AddsAndUpdatesAndDeactivates()
    {
        // Předchozí stav: jedna importovaná čtečka (zmizí z WIN-PAK) a jedna ruční.
        _db.Readers.Add(new Reader { ExternalId = "R-OLD", Name = "Stará", Source = RecordSource.Imported, IsActive = true });
        _db.Readers.Add(new Reader { Name = "Ruční čtečka", Source = RecordSource.Manual, IsActive = true });
        _db.Readers.Add(new Reader { ExternalId = "R-001", Name = "Původní název", Source = RecordSource.Imported, IsActive = true });
        await _db.SaveChangesAsync();

        var client = CreateWinPakClient(
            new { id = "R-001", name = "Hlavní vchod", description = "x", panelName = "P1", accountName = "A", isActive = true },
            new { id = "R-002", name = "Serverovna", description = (string?)null, panelName = "P1", accountName = "A", isActive = true });

        var sync = new ReaderSyncService(_db, client, _audit);
        var result = await sync.SyncAsync("test");

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Deactivated);

        var r001 = await _db.Readers.SingleAsync(r => r.ExternalId == "R-001");
        Assert.Equal("Hlavní vchod", r001.Name);

        var old = await _db.Readers.SingleAsync(r => r.ExternalId == "R-OLD");
        Assert.False(old.IsActive);

        var manual = await _db.Readers.SingleAsync(r => r.Source == RecordSource.Manual);
        Assert.True(manual.IsActive); // ruční záznamy synchronizace nesmí měnit
    }

    [Fact]
    public async Task EmployeeSync_FromApi_Upserts()
    {
        await _settings.SetAsync(SettingKeys.EmployeeSourceMode, "Api");
        await _settings.SetAsync(SettingKeys.EmployeeApiUrl, "http://hr/api/employees");

        _db.Employees.Add(new Employee
        {
            ExternalId = "E1", FirstName = "Jan", LastName = "Starý",
            Source = RecordSource.Imported, IsActive = true,
        });
        await _db.SaveChangesAsync();

        var employees = new[]
        {
            new { externalId = "E1", personalNumber = "1001", firstName = "Jan", lastName = "Novák",
                  email = (string?)"jan@fnmh.cz", department = "IT", adAccount = "jnovak", cardNumber = (string?)"100234" },
            new { externalId = "E2", personalNumber = "1002", firstName = "Petra", lastName = "Svobodová",
                  email = (string?)null, department = "HR", adAccount = "psvobodova", cardNumber = (string?)null },
        };
        var http = new HttpClient(new StubHandler(_ =>
            (HttpStatusCode.OK, JsonSerializer.Serialize(employees))));

        var source = new ApiEmployeeSource(http, _settings);
        var records = await source.FetchAsync();
        Assert.Equal(2, records.Count);

        // Upsert přes EmployeeSyncService s podvrženou factory
        var sync = new EmployeeSyncService(_db, new StubSourceFactory(source, _settings), _audit);
        var result = await sync.SyncAsync("test");

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Updated);

        var e1 = await _db.Employees.SingleAsync(e => e.ExternalId == "E1");
        Assert.Equal("Novák", e1.LastName);
        Assert.Equal("jnovak", e1.AdAccount);
    }

    private sealed class StubSourceFactory(IEmployeeSource source, SettingsService settings)
        : EmployeeSourceFactory(settings, null!)
    {
        public override Task<IEmployeeSource?> CreateAsync(CancellationToken ct = default)
            => Task.FromResult<IEmployeeSource?>(source);
    }
}
