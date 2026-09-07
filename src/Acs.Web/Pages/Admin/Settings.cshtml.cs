using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Integration;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.WinPak;
using Acs.Web.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Acs.Web.Pages.Admin;

public class SettingsModel(SettingsService settings, AuditService audit, WinPakClient winPak,
    ParkingConnectorClient parkingConnector) : PageModel
{
    public Dictionary<string, string?> Values { get; } = new();

    [TempData] public string? SavedSection { get; set; }
    [TempData] public string? WinPakTestResult { get; set; }
    [TempData] public string? ParkingSystemTestResult { get; set; }

    /// <summary>Je nastavený klíč integračního API (hodnota se nezobrazuje)?</summary>
    public bool ParkingApiKeySet { get; private set; }

    /// <summary>Absolutní adresa integračního API pro předání dodavateli parkovacího systému.</summary>
    public string IntegrationApiUrl => $"{Request.Scheme}://{Request.Host}{IntegrationApi.Prefix}";

    private static readonly string[] DisplayedKeys =
    [
        SettingKeys.AppTitle, SettingKeys.DefaultTheme,
        SettingKeys.LdapEnabled, SettingKeys.LdapServer, SettingKeys.LdapPort, SettingKeys.LdapUseSsl,
        SettingKeys.LdapBaseDn, SettingKeys.LdapDomain, SettingKeys.LdapUserFilter, SettingKeys.LdapGroupRoleMap,
        SettingKeys.LdapBindUser, SettingKeys.LdapUseDcLocator,
        SettingKeys.WinPakBaseUrl, SettingKeys.WinPakSyncEnabled, SettingKeys.WinPakSyncIntervalMinutes,
        SettingKeys.WinPakAccessSyncEnabled, SettingKeys.WinPakAccessSyncIntervalMinutes,
        SettingKeys.EmployeeSourceMode, SettingKeys.EmployeeMssqlQuery, SettingKeys.EmployeeApiUrl,
        SettingKeys.EmployeeLdapFilter, SettingKeys.EmployeeLdapPageSize, SettingKeys.EmployeeLdapTimeoutMinutes,
        SettingKeys.EmployeePersonalNumberAttribute,
        SettingKeys.CardsMssqlQuery, SettingKeys.CardsSyncEnabled, SettingKeys.CardsSyncIntervalMinutes,
        SettingKeys.AutomationEnabled, SettingKeys.AutomationIntervalMinutes, SettingKeys.AutoOffboardingEnabled,
        SettingKeys.AutoDepartmentChangeEnabled, SettingKeys.AutoExpirationEnabled, SettingKeys.AutoRemindersEnabled,
        SettingKeys.AutoReminderAfterDays, SettingKeys.AutoEscalationAfterDays, SettingKeys.AutoPushEnabled,
        SettingKeys.SmtpHost, SettingKeys.SmtpPort, SettingKeys.SmtpUser, SettingKeys.SmtpFrom,
        SettingKeys.SmtpUseTls,
        SettingKeys.ParkingSystemEnabled, SettingKeys.ParkingSystemAllowedIps, SettingKeys.ParkingSystemCacheTtlSeconds,
        SettingKeys.ParkingSystemExitAlwaysAllowed, SettingKeys.ParkingSystemConnectorBaseUrl,
        SettingKeys.ParkingSystemPushEnabled,
    ];

    /// <summary>Odkaz do administrace konektoru — vlastní nastavení má konektor u sebe na serveru.</summary>
    public string? WinPakAdminUrl { get; private set; }

    public async Task OnGetAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        foreach (var key in DisplayedKeys)
            Values[key] = await settings.GetAsync(key);
        ParkingApiKeySet = !string.IsNullOrEmpty(await settings.GetAsync(SettingKeys.ParkingSystemApiKey));

        WinPakAdminUrl = Values[SettingKeys.WinPakBaseUrl] is { Length: > 0 } baseUrl
                         && Uri.TryCreate(baseUrl.TrimEnd('/') + "/ui", UriKind.Absolute, out var uri)
            ? uri.ToString()
            : null;
    }

    private string UserName => User.Identity?.Name ?? "?";

    public async Task<IActionResult> OnPostGeneralAsync(string? appTitle, string? defaultTheme)
    {
        await settings.SetAsync(SettingKeys.AppTitle, appTitle, UserName);
        await settings.SetAsync(SettingKeys.DefaultTheme, defaultTheme, UserName);
        return await SavedAsync("Obecné");
    }

    public async Task<IActionResult> OnPostLdapAsync(
        string? ldapEnabled, string? ldapServer, string? ldapPort, string? ldapUseSsl,
        string? ldapBaseDn, string? ldapDomain, string? ldapUserFilter, string? ldapBindPassword,
        string? ldapGroupRoleMap, string? ldapBindUser, string? ldapUseDcLocator)
    {
        await settings.SetAsync(SettingKeys.LdapUseDcLocator, ldapUseDcLocator == "true" ? "true" : "false", UserName);
        await settings.SetAsync(SettingKeys.LdapBindUser, ldapBindUser, UserName);
        await settings.SetAsync(SettingKeys.LdapGroupRoleMap, ldapGroupRoleMap, UserName);
        await settings.SetAsync(SettingKeys.LdapEnabled, ldapEnabled == "true" ? "true" : "false", UserName);
        await settings.SetAsync(SettingKeys.LdapServer, ldapServer, UserName);
        await settings.SetAsync(SettingKeys.LdapPort, ldapPort, UserName);
        await settings.SetAsync(SettingKeys.LdapUseSsl, ldapUseSsl == "true" ? "true" : "false", UserName);
        await settings.SetAsync(SettingKeys.LdapBaseDn, ldapBaseDn, UserName);
        await settings.SetAsync(SettingKeys.LdapDomain, ldapDomain, UserName);
        await settings.SetAsync(SettingKeys.LdapUserFilter, ldapUserFilter, UserName);
        await settings.SetIfProvidedAsync(SettingKeys.LdapBindPassword, ldapBindPassword, UserName);
        return await SavedAsync("Active Directory");
    }

    public async Task<IActionResult> OnPostWinPakAsync(
        string? winPakBaseUrl, string? winPakApiKey, string? winPakSyncEnabled, string? winPakSyncIntervalMinutes,
        string? winPakAccessSyncEnabled, string? winPakAccessSyncIntervalMinutes)
    {
        await settings.SetAsync(SettingKeys.WinPakBaseUrl, winPakBaseUrl, UserName);
        await settings.SetIfProvidedAsync(SettingKeys.WinPakApiKey, winPakApiKey, UserName);
        await settings.SetAsync(SettingKeys.WinPakSyncEnabled, winPakSyncEnabled == "true" ? "true" : "false", UserName);
        await settings.SetAsync(SettingKeys.WinPakSyncIntervalMinutes, winPakSyncIntervalMinutes, UserName);
        await settings.SetAsync(SettingKeys.WinPakAccessSyncEnabled, winPakAccessSyncEnabled == "true" ? "true" : "false", UserName);
        await settings.SetAsync(SettingKeys.WinPakAccessSyncIntervalMinutes, winPakAccessSyncIntervalMinutes, UserName);
        return await SavedAsync("WIN-PAK");
    }

    public async Task<IActionResult> OnPostWinPakTestAsync(
        string? winPakBaseUrl, string? winPakApiKey, string? winPakSyncEnabled, string? winPakSyncIntervalMinutes,
        string? winPakAccessSyncEnabled, string? winPakAccessSyncIntervalMinutes)
    {
        await OnPostWinPakAsync(winPakBaseUrl, winPakApiKey, winPakSyncEnabled, winPakSyncIntervalMinutes,
            winPakAccessSyncEnabled, winPakAccessSyncIntervalMinutes);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var info = await winPak.GetInfoAsync(cts.Token);
            WinPakTestResult = info is null
                ? "konektor neodpověděl"
                : $"OK — režim {info.ProviderMode}, verze {info.Version}, zápis: {(info.SupportsWrite ? "ano" : "ne")}";
        }
        catch (Exception ex)
        {
            WinPakTestResult = $"Chyba: {ex.Message}";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEmployeesAsync(
        string? employeeSourceMode, string? employeeMssqlConnectionString,
        string? employeeMssqlQuery, string? employeeApiUrl, string? employeeApiKey,
        string? employeeLdapFilter, string? employeeLdapPageSize, string? employeeLdapTimeoutMinutes,
        string? employeePersonalNumberAttribute)
    {
        await settings.SetAsync(SettingKeys.EmployeeLdapPageSize, employeeLdapPageSize, UserName);
        await settings.SetAsync(SettingKeys.EmployeeLdapTimeoutMinutes, employeeLdapTimeoutMinutes, UserName);
        await settings.SetAsync(SettingKeys.EmployeeSourceMode, employeeSourceMode, UserName);
        await settings.SetIfProvidedAsync(SettingKeys.EmployeeMssqlConnectionString, employeeMssqlConnectionString, UserName);
        await settings.SetAsync(SettingKeys.EmployeeMssqlQuery, employeeMssqlQuery, UserName);
        await settings.SetAsync(SettingKeys.EmployeeApiUrl, employeeApiUrl, UserName);
        await settings.SetIfProvidedAsync(SettingKeys.EmployeeApiKey, employeeApiKey, UserName);
        await settings.SetAsync(SettingKeys.EmployeeLdapFilter, employeeLdapFilter, UserName);
        await settings.SetAsync(SettingKeys.EmployeePersonalNumberAttribute, employeePersonalNumberAttribute, UserName);
        return await SavedAsync("Zdroj zaměstnanců");
    }

    public async Task<IActionResult> OnPostAutomationAsync(
        string? automationEnabled, string? automationIntervalMinutes,
        string? autoOffboardingEnabled, string? autoDepartmentChangeEnabled, string? autoExpirationEnabled,
        string? autoRemindersEnabled, string? autoReminderAfterDays, string? autoEscalationAfterDays,
        string? autoPushEnabled)
    {
        await settings.SetAsync(SettingKeys.AutomationEnabled, Flag(automationEnabled), UserName);
        await settings.SetAsync(SettingKeys.AutomationIntervalMinutes, automationIntervalMinutes, UserName);
        await settings.SetAsync(SettingKeys.AutoOffboardingEnabled, Flag(autoOffboardingEnabled), UserName);
        await settings.SetAsync(SettingKeys.AutoDepartmentChangeEnabled, Flag(autoDepartmentChangeEnabled), UserName);
        await settings.SetAsync(SettingKeys.AutoExpirationEnabled, Flag(autoExpirationEnabled), UserName);
        await settings.SetAsync(SettingKeys.AutoRemindersEnabled, Flag(autoRemindersEnabled), UserName);
        await settings.SetAsync(SettingKeys.AutoReminderAfterDays, autoReminderAfterDays, UserName);
        await settings.SetAsync(SettingKeys.AutoEscalationAfterDays, autoEscalationAfterDays, UserName);
        await settings.SetAsync(SettingKeys.AutoPushEnabled, Flag(autoPushEnabled), UserName);
        return await SavedAsync("Automatizace");
    }

    private static string Flag(string? value) => value == "true" ? "true" : "false";

    public async Task<IActionResult> OnPostCardsAsync(
        string? cardsMssqlConnectionString, string? cardsMssqlQuery,
        string? cardsSyncEnabled, string? cardsSyncIntervalMinutes)
    {
        await settings.SetIfProvidedAsync(SettingKeys.CardsMssqlConnectionString, cardsMssqlConnectionString, UserName);
        await settings.SetAsync(SettingKeys.CardsMssqlQuery, cardsMssqlQuery, UserName);
        await settings.SetAsync(SettingKeys.CardsSyncEnabled, cardsSyncEnabled == "true" ? "true" : "false", UserName);
        await settings.SetAsync(SettingKeys.CardsSyncIntervalMinutes, cardsSyncIntervalMinutes, UserName);
        return await SavedAsync("Karty");
    }

    public async Task<IActionResult> OnPostSmtpAsync(
        string? smtpHost, string? smtpPort, string? smtpUser, string? smtpPassword, string? smtpFrom,
        string? smtpUseTls)
    {
        await settings.SetAsync(SettingKeys.SmtpHost, smtpHost, UserName);
        await settings.SetAsync(SettingKeys.SmtpPort, smtpPort, UserName);
        await settings.SetAsync(SettingKeys.SmtpUser, smtpUser, UserName);
        await settings.SetIfProvidedAsync(SettingKeys.SmtpPassword, smtpPassword, UserName);
        await settings.SetAsync(SettingKeys.SmtpFrom, smtpFrom, UserName);
        await settings.SetAsync(SettingKeys.SmtpUseTls, smtpUseTls == "true" ? "true" : "false", UserName);
        return await SavedAsync("SMTP");
    }

    public async Task<IActionResult> OnPostParkingSystemAsync(
        string? parkingSystemEnabled, string? parkingSystemApiKey, string? parkingSystemAllowedIps,
        string? parkingSystemCacheTtlSeconds, string? parkingSystemExitAlwaysAllowed,
        string? parkingSystemConnectorBaseUrl, string? parkingSystemConnectorApiKey, string? parkingSystemPushEnabled)
    {
        await settings.SetAsync(SettingKeys.ParkingSystemEnabled, Flag(parkingSystemEnabled), UserName);
        await settings.SetIfProvidedAsync(SettingKeys.ParkingSystemApiKey, parkingSystemApiKey, UserName);
        await settings.SetAsync(SettingKeys.ParkingSystemAllowedIps, parkingSystemAllowedIps, UserName);
        await settings.SetAsync(SettingKeys.ParkingSystemCacheTtlSeconds, parkingSystemCacheTtlSeconds, UserName);
        await settings.SetAsync(SettingKeys.ParkingSystemExitAlwaysAllowed, Flag(parkingSystemExitAlwaysAllowed), UserName);
        await settings.SetAsync(SettingKeys.ParkingSystemConnectorBaseUrl, parkingSystemConnectorBaseUrl, UserName);
        await settings.SetIfProvidedAsync(SettingKeys.ParkingSystemConnectorApiKey, parkingSystemConnectorApiKey, UserName);
        await settings.SetAsync(SettingKeys.ParkingSystemPushEnabled, Flag(parkingSystemPushEnabled), UserName);
        return await SavedAsync("Parkovací systém");
    }

    public async Task<IActionResult> OnPostParkingSystemTestAsync(
        string? parkingSystemEnabled, string? parkingSystemApiKey, string? parkingSystemAllowedIps,
        string? parkingSystemCacheTtlSeconds, string? parkingSystemExitAlwaysAllowed,
        string? parkingSystemConnectorBaseUrl, string? parkingSystemConnectorApiKey, string? parkingSystemPushEnabled)
    {
        await OnPostParkingSystemAsync(parkingSystemEnabled, parkingSystemApiKey, parkingSystemAllowedIps,
            parkingSystemCacheTtlSeconds, parkingSystemExitAlwaysAllowed, parkingSystemConnectorBaseUrl,
            parkingSystemConnectorApiKey, parkingSystemPushEnabled);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (!await parkingConnector.IsConfiguredAsync(cts.Token))
            {
                ParkingSystemTestResult = "adresa konektoru není vyplněná — ACS do parkovacího systému nezapisuje "
                                          + "(online autorizace a hlášení událostí přes integrační API fungují i bez konektoru)";
            }
            else
            {
                var caps = await parkingConnector.GetCapabilitiesAsync(cts.Token);
                ParkingSystemTestResult = caps is null
                    ? "konektor neodpověděl"
                    : $"OK — {caps.Connector.Name} {caps.Connector.Version}"
                      + (caps.Connector.TargetSystem is { Length: > 0 } t ? $" ({t})" : "")
                      + $", kontrakt {caps.ContractVersion}, operace: {string.Join(", ", caps.Operations)}"
                      + (caps.SupportsValidity == false ? " — POZOR: systém neumí platnost, expirace řeší ACS" : "");
            }
        }
        catch (Exception ex)
        {
            ParkingSystemTestResult = $"Chyba: {ex.Message}";
        }

        return RedirectToPage();
    }

    private async Task<IActionResult> SavedAsync(string section)
    {
        await audit.LogAsync(UserName, "settings-updated", "Settings", section);
        SavedSection = section;
        return RedirectToPage();
    }
}
