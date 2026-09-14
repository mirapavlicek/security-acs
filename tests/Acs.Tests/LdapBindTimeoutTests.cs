using System.DirectoryServices.Protocols;
using System.Net;
using System.Net.Sockets;
using Acs.Infrastructure.Auth;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acs.Tests;

/// <summary>
/// Řadič, který TCP spojení přijme, ale nikdy neodpoví (zaseknutý TLS/LDAP) — dřív držel
/// přihlášení do vypršení TCP a přihlašovací stránka končila 504 od reverzní proxy.
/// </summary>
public sealed class LdapBindTimeoutTests : IDisposable
{
    private readonly TcpListener _silentDc = new(IPAddress.Loopback, 0);
    private readonly List<TcpClient> _accepted = []; // drží se, aby GC spojení nezavřel (to by vrátilo „server down“, ne timeout)
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AcsDbContext _db;
    private readonly SettingsService _settings;

    public LdapBindTimeoutTests()
    {
        _silentDc.Start();
        _ = AcceptForeverAsync();
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _settings = new SettingsService(_db, new EphemeralDataProtectionProvider());
        DcLocator.Invalidate();
    }

    private int Port => ((IPEndPoint)_silentDc.LocalEndpoint).Port;

    private async Task AcceptForeverAsync()
    {
        try
        {
            while (true)
            {
                var client = await _silentDc.AcceptTcpClientAsync(); // spojení se drží otevřené, nic se nepošle
                lock (_accepted)
                    _accepted.Add(client);
            }
        }
        catch (Exception)
        {
            // listener zastaven
        }
    }

    public void Dispose()
    {
        DcLocator.Invalidate();
        _silentDc.Stop();
        lock (_accepted)
            _accepted.ForEach(c => c.Dispose());
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Bind_k_mlcicimu_radici_skonci_v_limitu_chybou_spojeni()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<LdapException>(() =>
            LdapAuthenticator.OpenAsync("127.0.0.1", Port, useSsl: false, "jnovak@nnh.local", "pwd", TimeSpan.FromSeconds(1)));

        Assert.Equal(LdapAuthenticator.LdapTimeoutErrorCode, ex.ErrorCode);
        Assert.True(LdapAuthenticator.IsConnectivityError(ex));
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        Assert.Contains("nedokončil", ex.Message);
    }

    [Fact]
    public async Task Prihlaseni_pres_zaseknuty_radic_hlasi_nedostupne_AD_misto_cekani()
    {
        await _settings.SetAsync(SettingKeys.LdapEnabled, "true");
        await _settings.SetAsync(SettingKeys.LdapUseDcLocator, "false");
        await _settings.SetAsync(SettingKeys.LdapUseSsl, "false");
        await _settings.SetAsync(SettingKeys.LdapServer, "127.0.0.1");
        await _settings.SetAsync(SettingKeys.LdapPort, Port.ToString());
        var locator = new DcLocator(_settings, NullLogger<DcLocator>.Instance);
        var authenticator = new LdapAuthenticator(_settings, locator, NullLogger<LdapAuthenticator>.Instance);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<LdapUnavailableException>(() => authenticator.AuthenticateAsync("jnovak", "pwd"));

        // Dva pokusy (řadič + náhradní) po LoginBindTimeout, s rezervou — pod limit proxy.
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, LdapAuthenticator.LoginBindTimeout * 2 + TimeSpan.FromSeconds(10));
        Assert.Contains("neodpovídá", ex.Message);
    }
}
