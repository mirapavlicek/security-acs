using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.WinPak;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Acs.Web.Pages.Admin;

public class SettingsModel(SettingsService settings, AuditService audit, WinPakClient winPak) : PageModel
{
    public Dictionary<string, string?> Values { get; } = new();

    [TempData] public string? SavedSection { get; set; }
    [TempData] public string? WinPakTestResult { get; set; }

    private static readonly string[] DisplayedKeys =
    [
        SettingKeys.AppTitle, SettingKeys.DefaultTheme,
        SettingKeys.LdapEnabled, SettingKeys.LdapServer, SettingKeys.LdapPort, SettingKeys.LdapUseSsl,
        SettingKeys.LdapBaseDn, SettingKeys.LdapDomain, SettingKeys.LdapUserFilter, SettingKeys.LdapGroupRoleMap,
        SettingKeys.LdapBindUser,
        SettingKeys.WinPakBaseUrl, SettingKeys.WinPakSyncEnabled, SettingKeys.WinPakSyncIntervalMinutes,
        SettingKeys.WinPakAccessSyncEnabled, SettingKeys.WinPakAccessSyncIntervalMinutes,
        SettingKeys.EmployeeSourceMode, SettingKeys.EmployeeMssqlQuery, SettingKeys.EmployeeApiUrl,
        SettingKeys.EmployeeLdapFilter,
        SettingKeys.CardsMssqlQuery, SettingKeys.CardsSyncEnabled, SettingKeys.CardsSyncIntervalMinutes,
        SettingKeys.AutomationEnabled, SettingKeys.AutomationIntervalMinutes, SettingKeys.AutoOffboardingEnabled,
        SettingKeys.AutoDepartmentChangeEnabled, SettingKeys.AutoExpirationEnabled, SettingKeys.AutoRemindersEnabled,
        SettingKeys.AutoReminderAfterDays, SettingKeys.AutoEscalationAfterDays, SettingKeys.AutoPushEnabled,
        SettingKeys.SmtpHost, SettingKeys.SmtpPort, SettingKeys.SmtpUser, SettingKeys.SmtpFrom,
        SettingKeys.SmtpUseTls,
    ];

    public async Task OnGetAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        foreach (var key in DisplayedKeys)
            Values[key] = await settings.GetAsync(key);
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
        string? ldapGroupRoleMap, string? ldapBindUser)
    {
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
        string? employeeLdapFilter)
    {
        await settings.SetAsync(SettingKeys.EmployeeSourceMode, employeeSourceMode, UserName);
        await settings.SetIfProvidedAsync(SettingKeys.EmployeeMssqlConnectionString, employeeMssqlConnectionString, UserName);
        await settings.SetAsync(SettingKeys.EmployeeMssqlQuery, employeeMssqlQuery, UserName);
        await settings.SetAsync(SettingKeys.EmployeeApiUrl, employeeApiUrl, UserName);
        await settings.SetIfProvidedAsync(SettingKeys.EmployeeApiKey, employeeApiKey, UserName);
        await settings.SetAsync(SettingKeys.EmployeeLdapFilter, employeeLdapFilter, UserName);
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

    private async Task<IActionResult> SavedAsync(string section)
    {
        await audit.LogAsync(UserName, "settings-updated", "Settings", section);
        SavedSection = section;
        return RedirectToPage();
    }
}
