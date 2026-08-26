using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Settings;

/// <summary>Známé klíče nastavení (vše se edituje v GUI).</summary>
public static class SettingKeys
{
    // Obecné
    public const string AppTitle = "App:Title";
    public const string DefaultTheme = "App:DefaultTheme";

    // Active Directory / LDAP
    public const string LdapEnabled = "Ldap:Enabled";
    public const string LdapServer = "Ldap:Server";
    public const string LdapPort = "Ldap:Port";
    public const string LdapUseSsl = "Ldap:UseSsl";
    public const string LdapBaseDn = "Ldap:BaseDn";
    public const string LdapBindUser = "Ldap:BindUser";
    public const string LdapBindPassword = "Ldap:BindPassword";      // secret
    public const string LdapUserFilter = "Ldap:UserFilter";
    public const string LdapDomain = "Ldap:Domain";

    /// <summary>Mapování AD skupin na role, řádky ve tvaru „NázevSkupiny=Role1,Role2“.</summary>
    public const string LdapGroupRoleMap = "Ldap:GroupRoleMap";

    // WinPak Connector
    public const string WinPakBaseUrl = "WinPak:BaseUrl";
    public const string WinPakApiKey = "WinPak:ApiKey";              // secret
    public const string WinPakSyncEnabled = "WinPak:SyncEnabled";
    public const string WinPakSyncIntervalMinutes = "WinPak:SyncIntervalMinutes";

    // Zpětná synchronizace stavu přístupů z WIN-PAK do ACS
    public const string WinPakAccessSyncEnabled = "WinPak:AccessSyncEnabled";
    public const string WinPakAccessSyncIntervalMinutes = "WinPak:AccessSyncIntervalMinutes";

    // Zdroj zaměstnanců
    public const string EmployeeSourceMode = "Employees:SourceMode"; // None | Mssql | Api
    public const string EmployeeMssqlConnectionString = "Employees:MssqlConnectionString"; // secret
    public const string EmployeeMssqlQuery = "Employees:MssqlQuery";
    public const string EmployeeApiUrl = "Employees:ApiUrl";
    public const string EmployeeApiKey = "Employees:ApiKey";         // secret
    public const string EmployeeSyncEnabled = "Employees:SyncEnabled";
    public const string EmployeeSyncIntervalMinutes = "Employees:SyncIntervalMinutes";

    // SMTP notifikace
    public const string SmtpHost = "Smtp:Host";
    public const string SmtpPort = "Smtp:Port";
    public const string SmtpUser = "Smtp:User";
    public const string SmtpPassword = "Smtp:Password";              // secret
    public const string SmtpFrom = "Smtp:From";
    public const string SmtpUseTls = "Smtp:UseTls";

    public static readonly HashSet<string> SecretKeys =
    [
        LdapBindPassword, WinPakApiKey, EmployeeMssqlConnectionString, EmployeeApiKey, SmtpPassword,
    ];
}

/// <summary>
/// Nastavení aplikace v DB (sdílené oběma nody). Citlivé hodnoty jsou šifrované
/// pomocí Data Protection (klíče rovněž v DB → oba nody dešifrují stejně).
/// </summary>
public class SettingsService(AcsDbContext db, IDataProtectionProvider dataProtection)
{
    private const string Purpose = "Acs.Settings.v1";
    private readonly IDataProtector _protector = dataProtection.CreateProtector(Purpose);

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var setting = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting?.Value is null)
            return null;
        return setting.IsSecret ? _protector.Unprotect(setting.Value) : setting.Value;
    }

    public async Task<bool> GetBoolAsync(string key, bool defaultValue = false, CancellationToken ct = default)
        => await GetAsync(key, ct) is { } v ? v is "true" or "1" or "True" : defaultValue;

    public async Task<int> GetIntAsync(string key, int defaultValue, CancellationToken ct = default)
        => int.TryParse(await GetAsync(key, ct), out var v) ? v : defaultValue;

    public async Task SetAsync(string key, string? value, string? updatedBy = null, CancellationToken ct = default)
    {
        var isSecret = SettingKeys.SecretKeys.Contains(key);
        var stored = value is null ? null : isSecret ? _protector.Protect(value) : value;

        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
        {
            db.Settings.Add(new Setting
            {
                Key = key, Value = stored, IsSecret = isSecret,
                UpdatedAt = DateTime.UtcNow, UpdatedBy = updatedBy,
            });
        }
        else
        {
            setting.Value = stored;
            setting.IsSecret = isSecret;
            setting.UpdatedAt = DateTime.UtcNow;
            setting.UpdatedBy = updatedBy;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Uloží hodnotu jen pokud není prázdná (u secretů „ponechat stávající“).</summary>
    public async Task SetIfProvidedAsync(string key, string? value, string? updatedBy = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(value))
            await SetAsync(key, value, updatedBy, ct);
    }
}
