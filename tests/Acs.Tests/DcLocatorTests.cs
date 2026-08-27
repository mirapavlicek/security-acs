using Acs.Infrastructure.Auth;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acs.Tests;

public sealed class DcLocatorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly SettingsService _settings;

    public DcLocatorTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _settings = new SettingsService(_db, new EphemeralDataProtectionProvider());
        DcLocator.Invalidate(); // izolace mezi testy (statická cache)
    }

    public void Dispose()
    {
        DcLocator.Invalidate();
        _db.Dispose();
        _connection.Dispose();
    }

    private DcLocator Create(
        IReadOnlyList<DcCandidate>? srv = null,
        Func<string, bool>? healthy = null,
        Exception? srvError = null)
        => new(_settings, NullLogger<DcLocator>.Instance,
            srvQuery: (_, _) => srvError is null
                ? Task.FromResult(srv ?? [])
                : Task.FromException<IReadOnlyList<DcCandidate>>(srvError),
            probe: (host, _, _) => Task.FromResult(healthy?.Invoke(host) ?? true));

    [Fact]
    public void OrderCandidates_SortsByPriorityThenWeight()
    {
        var ordered = DcLocator.OrderCandidates(
        [
            new DcCandidate("dc-low-weight.x.", 0, 10),
            new DcCandidate("dc-backup.x.", 10, 100),
            new DcCandidate("dc-primary.x.", 0, 100),
        ]);
        Assert.Equal(["dc-primary.x", "dc-low-weight.x", "dc-backup.x"], ordered);
    }

    [Fact]
    public async Task ActiveServer_IsFirstHealthySrvCandidate()
    {
        await _settings.SetAsync(SettingKeys.LdapDomain, "nnh.local");
        var locator = Create(
            srv: [new DcCandidate("dc1.nnh.local.", 0, 100), new DcCandidate("dc2.nnh.local.", 0, 50)],
            healthy: host => host == "dc2.nnh.local"); // dc1 „leží“

        Assert.Equal("dc2.nnh.local", await locator.GetActiveServerAsync());
    }

    [Fact]
    public async Task FallsBackToConfiguredServer_WhenSrvFails()
    {
        await _settings.SetAsync(SettingKeys.LdapDomain, "nnh.local");
        await _settings.SetAsync(SettingKeys.LdapServer, "dc-manual.nnh.local");
        var locator = Create(srvError: new InvalidOperationException("DNS timeout"));

        Assert.Equal("dc-manual.nnh.local", await locator.GetActiveServerAsync());
    }

    [Fact]
    public async Task LocatorDisabled_UsesConfiguredServerOnly()
    {
        await _settings.SetAsync(SettingKeys.LdapUseDcLocator, "false");
        await _settings.SetAsync(SettingKeys.LdapDomain, "nnh.local");
        await _settings.SetAsync(SettingKeys.LdapServer, "dc-manual.nnh.local");
        var locator = Create(srv: [new DcCandidate("dc-from-srv.x", 0, 1)]);

        var candidates = await locator.GetCandidateServersAsync();
        Assert.Equal(["dc-manual.nnh.local"], candidates);
    }

    [Fact]
    public async Task Throws_WhenNothingAvailable()
    {
        await _settings.SetAsync(SettingKeys.LdapDomain, "nnh.local");
        var locator = Create(srv: [new DcCandidate("dc1.x", 0, 1)], healthy: _ => false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => locator.GetActiveServerAsync());
    }

    [Fact]
    public async Task ActiveServer_IsCached_UntilInvalidated()
    {
        await _settings.SetAsync(SettingKeys.LdapDomain, "nnh.local");
        var calls = 0;
        var locator = new DcLocator(_settings, NullLogger<DcLocator>.Instance,
            srvQuery: (_, _) =>
            {
                calls++;
                return Task.FromResult<IReadOnlyList<DcCandidate>>([new DcCandidate("dc1.x", 0, 1)]);
            },
            probe: (_, _, _) => Task.FromResult(true));

        await locator.GetActiveServerAsync();
        await locator.GetActiveServerAsync();
        Assert.Equal(1, calls); // druhé volání jde z cache

        DcLocator.Invalidate();
        await locator.GetActiveServerAsync();
        Assert.Equal(2, calls);
    }
}
