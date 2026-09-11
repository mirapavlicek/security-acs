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

    /// <summary>DC lokátor: hledat aktivní řadič přes DNS SRV (_ldap._tcp.dc._msdcs).</summary>
    public const string LdapUseDcLocator = "Ldap:UseDcLocator";

    // Přihlášení účtem Windows (Negotiate: Kerberos / NTLM) — jednotné přihlášení z doménových PC
    /// <summary>Zapíná endpoint <c>/Account/WindowsLogin</c> (výzva <c>WWW-Authenticate: Negotiate</c>).</summary>
    public const string SsoEnabled = "Sso:Enabled";
    /// <summary>Přihlašovací stránka rovnou přesměruje na přihlášení Windows; formulář zůstává pod „manual=1“.</summary>
    public const string SsoAutoLogin = "Sso:AutoLogin";
    /// <summary>Povolené domény / realm-y účtu (NetBIOS nebo DNS, oddělené čárkou; prázdné = libovolná).</summary>
    public const string SsoAllowedDomains = "Sso:AllowedDomains";

    // WinPak Connector
    public const string WinPakBaseUrl = "WinPak:BaseUrl";
    public const string WinPakApiKey = "WinPak:ApiKey";              // secret
    public const string WinPakSyncEnabled = "WinPak:SyncEnabled";
    public const string WinPakSyncIntervalMinutes = "WinPak:SyncIntervalMinutes";

    // Zpětná synchronizace stavu přístupů z WIN-PAK do ACS
    public const string WinPakAccessSyncEnabled = "WinPak:AccessSyncEnabled";
    public const string WinPakAccessSyncIntervalMinutes = "WinPak:AccessSyncIntervalMinutes";

    // Zdroj zaměstnanců
    public const string EmployeeSourceMode = "Employees:SourceMode"; // None | Ad | Mssql | Api
    /// <summary>LDAP filtr pro výběr zaměstnanců při režimu Ad.</summary>
    public const string EmployeeLdapFilter = "Employees:LdapFilter";
    /// <summary>Velikost stránky LDAP dotazu (AD zvládá max 1000).</summary>
    public const string EmployeeLdapPageSize = "Employees:LdapPageSize";

    /// <summary>Ze kterého AD atributu brát osobní číslo (víc jich lze oddělit čárkou, bere se první neprázdný).</summary>
    public const string EmployeePersonalNumberAttribute = "Employees:LdapPersonalNumberAttribute";
    /// <summary>Timeout LDAP dotazu v minutách (velké domény trvají déle).</summary>
    public const string EmployeeLdapTimeoutMinutes = "Employees:LdapTimeoutMinutes";
    /// <summary>AD atribut s nadřízeným (DN nadřízeného); výchozí <c>manager</c>.</summary>
    public const string EmployeeLdapManagerAttribute = "Employees:LdapManagerAttribute";
    /// <summary>Statistika nadřízených z poslední synchronizace („spárováno X z Y, zdroj uvádí Z“) — jen k zobrazení.</summary>
    public const string EmployeeLastManagerStats = "Employees:LastManagerStats";

    // Schvalování
    /// <summary>Id schvalovací matice pro žádosti o kamery / EZS (prázdné = výchozí matice, jinak administrátor).</summary>
    public const string SecurityMatrixId = "Security:MatrixId";

    // Karty z MSSQL (zaměstnanci z AD, karty z SQL)
    public const string CardsMssqlConnectionString = "Cards:MssqlConnectionString"; // secret
    public const string CardsMssqlQuery = "Cards:MssqlQuery";
    public const string CardsSyncEnabled = "Cards:SyncEnabled";
    public const string CardsSyncIntervalMinutes = "Cards:SyncIntervalMinutes";

    // Zdroj karet: Mssql (výchozí) nebo Api — integrační služba POST …/Identifiers { employeeNo, idIdentifierSubType }
    public const string CardsSource = "Cards:Source";
    public const string CardsApiUrl = "Cards:ApiUrl";
    public const string CardsApiSubType = "Cards:ApiSubType";
    /// <summary>None | ApiKey | Basic | Windows | Bearer (pevný token) | Token (přihlášení na endpoint služby)</summary>
    public const string CardsApiAuth = "Cards:ApiAuth";
    public const string CardsApiBearerToken = "Cards:ApiBearerToken";     // secret
    public const string CardsApiTokenUrl = "Cards:ApiTokenUrl";
    /// <summary>Tělo přihlášení; {user} a {password} se nahradí. Výchozí {"username":"{user}","password":"{password}"}.</summary>
    public const string CardsApiTokenBody = "Cards:ApiTokenBody";
    /// <summary>Název pole s tokenem v odpovědi přihlášení; prázdné = najde se (token, accessToken, access_token, jwt…).</summary>
    public const string CardsApiTokenField = "Cards:ApiTokenField";
    public const string CardsApiKeyHeader = "Cards:ApiKeyHeader";
    public const string CardsApiKey = "Cards:ApiKey";           // secret
    public const string CardsApiUser = "Cards:ApiUser";
    public const string CardsApiPassword = "Cards:ApiPassword"; // secret
    public const string CardsApiIgnoreTls = "Cards:ApiIgnoreTls";
    public const string EmployeeMssqlConnectionString = "Employees:MssqlConnectionString"; // secret
    public const string EmployeeMssqlQuery = "Employees:MssqlQuery";
    public const string EmployeeApiUrl = "Employees:ApiUrl";
    public const string EmployeeApiKey = "Employees:ApiKey";         // secret
    public const string EmployeeSyncEnabled = "Employees:SyncEnabled";
    public const string EmployeeSyncIntervalMinutes = "Employees:SyncIntervalMinutes";

    // Automatizace
    public const string AutomationEnabled = "Automation:Enabled";
    public const string AutomationIntervalMinutes = "Automation:IntervalMinutes";
    public const string AutoOffboardingEnabled = "Automation:OffboardingEnabled";
    public const string AutoDepartmentChangeEnabled = "Automation:DepartmentChangeEnabled";
    public const string AutoExpirationEnabled = "Automation:ExpirationEnabled";
    public const string AutoRemindersEnabled = "Automation:RemindersEnabled";
    public const string AutoReminderAfterDays = "Automation:ReminderAfterDays";
    public const string AutoEscalationAfterDays = "Automation:EscalationAfterDays";
    public const string AutoPushEnabled = "Automation:PushToWinPakEnabled";

    // SMTP notifikace
    public const string SmtpHost = "Smtp:Host";
    public const string SmtpPort = "Smtp:Port";
    public const string SmtpUser = "Smtp:User";
    public const string SmtpPassword = "Smtp:Password";              // secret
    public const string SmtpFrom = "Smtp:From";
    public const string SmtpUseTls = "Smtp:UseTls";

    // Parkovací systém (GreenCenter) — integrační API ACS (vzor B/C) a konektor (vzor A)
    /// <summary>Zapíná integrační API <c>/api/integration/v1</c> pro parkovací systém.</summary>
    public const string ParkingSystemEnabled = "ParkingSystem:Enabled";
    /// <summary>Klíč, kterým se parkovací systém prokazuje ACS (hlavička <c>X-Api-Key</c>).</summary>
    public const string ParkingSystemApiKey = "ParkingSystem:ApiKey";                 // secret
    /// <summary>Povolené IP adresy volajícího (oddělené čárkou; prázdné = bez omezení).</summary>
    public const string ParkingSystemAllowedIps = "ParkingSystem:AllowedIps";
    /// <summary>Jak dlouho smí parkovací systém rozhodnutí kešovat při výpadku ACS (s).</summary>
    public const string ParkingSystemCacheTtlSeconds = "ParkingSystem:CacheTtlSeconds";
    /// <summary>Výjezd se povolí vždy (vozidlo nesmí zůstat zavřené v areálu).</summary>
    public const string ParkingSystemExitAlwaysAllowed = "ParkingSystem:ExitAlwaysAllowed";
    /// <summary>Adresa konektoru parkovacího systému (connector-api.yaml); prázdné = bez zápisu.</summary>
    public const string ParkingSystemConnectorBaseUrl = "ParkingSystem:ConnectorBaseUrl";
    public const string ParkingSystemConnectorApiKey = "ParkingSystem:ConnectorApiKey"; // secret
    /// <summary>Předávat vydaná / odebraná povolení do konektoru automaticky.</summary>
    public const string ParkingSystemPushEnabled = "ParkingSystem:PushEnabled";

    public static readonly HashSet<string> SecretKeys =
    [
        LdapBindPassword, WinPakApiKey, EmployeeMssqlConnectionString, EmployeeApiKey, SmtpPassword,
        CardsMssqlConnectionString, CardsApiKey, CardsApiPassword, CardsApiBearerToken, ParkingSystemApiKey, ParkingSystemConnectorApiKey,
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
