using Acs.Infrastructure.Data;
using Acs.Infrastructure.Notifications;
using Acs.Infrastructure.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Acs.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly SettingsService _settings;

    public SettingsServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options);
        _db.Database.EnsureCreated();
        _settings = new SettingsService(_db, new EphemeralDataProtectionProvider());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task PlainValue_Roundtrip()
    {
        await _settings.SetAsync(SettingKeys.LdapServer, "dc01.fnmh.local", "test");
        Assert.Equal("dc01.fnmh.local", await _settings.GetAsync(SettingKeys.LdapServer));
    }

    [Fact]
    public async Task SecretValue_IsEncryptedAtRest_AndDecryptsOnRead()
    {
        await _settings.SetAsync(SettingKeys.WinPakApiKey, "super-tajny-klic", "test");

        var raw = await _db.Settings.AsNoTracking().SingleAsync(s => s.Key == SettingKeys.WinPakApiKey);
        Assert.True(raw.IsSecret);
        Assert.NotNull(raw.Value);
        Assert.DoesNotContain("super-tajny-klic", raw.Value);

        Assert.Equal("super-tajny-klic", await _settings.GetAsync(SettingKeys.WinPakApiKey));
    }

    [Fact]
    public async Task SetIfProvided_EmptyValue_KeepsExisting()
    {
        await _settings.SetAsync(SettingKeys.SmtpPassword, "puvodni", "test");
        await _settings.SetIfProvidedAsync(SettingKeys.SmtpPassword, "", "test");
        Assert.Equal("puvodni", await _settings.GetAsync(SettingKeys.SmtpPassword));
    }

    [Fact]
    public async Task GetBool_And_GetInt_ParseValues()
    {
        await _settings.SetAsync(SettingKeys.LdapEnabled, "true");
        await _settings.SetAsync(SettingKeys.LdapPort, "636");
        Assert.True(await _settings.GetBoolAsync(SettingKeys.LdapEnabled));
        Assert.Equal(636, await _settings.GetIntAsync(SettingKeys.LdapPort, 0));
        Assert.Equal(42, await _settings.GetIntAsync("neexistuje", 42));
    }

    /// <summary>
    /// Odkazy v e-mailech byly natvrdo „http://acs.fnmh.network/…“ — po přejmenování domény
    /// chodily mrtvé. Adresa je teď nastavení; bez něj platí správná výchozí a lomítko na konci nevadí.
    /// </summary>
    [Theory]
    [InlineData(null, "https://acs.fnmh.hospital")]
    [InlineData("", "https://acs.fnmh.hospital")]
    [InlineData("  https://acs-test.fnmh.hospital/  ", "https://acs-test.fnmh.hospital")]
    public void Verejna_adresa_pro_odkazy_v_mailech_ma_spravnou_vychozi_a_bez_koncoveho_lomitka(string? configured, string expected)
    {
        Assert.Equal(expected, EmailNotificationService.NormalizePublicUrl(configured));
        Assert.DoesNotContain("fnmh.network", SettingKeys.DefaultPublicUrl);
    }
}
