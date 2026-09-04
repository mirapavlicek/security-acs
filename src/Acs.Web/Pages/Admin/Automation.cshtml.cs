using System.Globalization;
using Acs.Infrastructure.Automation;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.Sync;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Acs.Web.Pages.Admin;

public class AutomationModel(
    SettingsService settings,
    AutomationService automation,
    HealthCheckService health,
    ReaderSyncService readerSync,
    SyncJobRunner jobs,
    EmployeeSyncService employeeSync,
    CardSyncService cardSync,
    AccessSyncService accessSync,
    AutoAssignmentService autoAssign) : PageModel
{
    public record JobStatus(string Name, bool Enabled, int IntervalMinutes, DateTime? LastRun);

    public List<JobStatus> Jobs { get; private set; } = [];
    public List<HealthItem> Health { get; private set; } = [];

    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync()
    {
        Jobs =
        [
            await JobAsync("Synchronizace čteček (WIN-PAK)", SettingKeys.WinPakSyncEnabled,
                SettingKeys.WinPakSyncIntervalMinutes, 60, "Sync:ReadersLastRunUtc"),
            await JobAsync("Synchronizace zaměstnanců (AD)", SettingKeys.EmployeeSyncEnabled,
                SettingKeys.EmployeeSyncIntervalMinutes, 60, "Sync:EmployeesLastRunUtc"),
            await JobAsync("Synchronizace karet (SQL)", SettingKeys.CardsSyncEnabled,
                SettingKeys.CardsSyncIntervalMinutes, 60, "Sync:CardsLastRunUtc"),
            await JobAsync("Zpětná synchronizace stavu z WIN-PAK", SettingKeys.WinPakAccessSyncEnabled,
                SettingKeys.WinPakAccessSyncIntervalMinutes, 15, "Sync:AccessLastRunUtc"),
            await JobAsync("Automatizace (offboarding, expirace, připomínky…)", SettingKeys.AutomationEnabled,
                SettingKeys.AutomationIntervalMinutes, 30, "Sync:AutomationLastRunUtc", defaultEnabled: true),
        ];

        Health = await health.RunAsync();
    }

    private async Task<JobStatus> JobAsync(string name, string enabledKey, string intervalKey,
        int defaultInterval, string lastRunKey, bool defaultEnabled = false)
    {
        var lastRunRaw = await settings.GetAsync(lastRunKey);
        DateTime? lastRun = DateTime.TryParse(lastRunRaw, null, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
        return new JobStatus(
            name,
            await settings.GetBoolAsync(enabledKey, defaultEnabled),
            await settings.GetIntAsync(intervalKey, defaultInterval),
            lastRun);
    }

    public async Task<IActionResult> OnPostRunAutomationAsync()
    {
        var result = await automation.RunAsync(User.Identity?.Name);
        Message = result.AnythingDone ? $"Automatizace: {result}." : "Automatizace proběhla — nebylo co řešit.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRunAllAsync()
    {
        var messages = new List<string>();
        var user = User.Identity?.Name;

        await TryAsync(messages, "čtečky", async () => (await readerSync.SyncAsync(user)).ToString());
        // Úrovně: jedno volání do WIN-PAKu na úroveň — na pozadí, ať požadavek nevyprší na proxy.
        var levelsStarted = jobs.Start(Acs.Web.Pages.Catalog.AccessLevels.IndexModel.SyncJob, async (services, ct)
            => (await services.GetRequiredService<AccessLevelSyncService>().SyncAsync(user, ct: ct)).ToString());
        messages.Add(levelsStarted ? "přístupové úrovně: spuštěno na pozadí (průběh v Katalog → Úrovně)" : "přístupové úrovně: už běží");
        await TryAsync(messages, "zaměstnanci", async () => (await employeeSync.SyncAsync(user)).ToString());
        await TryAsync(messages, "karty", async () => (await cardSync.SyncAsync(user)).ToString());
        await TryAsync(messages, "auto-zařazení", async () => (await autoAssign.RunAsync(user)).ToString());
        await TryAsync(messages, "stav z WIN-PAK", async () => (await accessSync.SyncAsync(user)).ToString());
        await TryAsync(messages, "automatizace", async () => (await automation.RunAsync(user)).ToString());

        Message = string.Join(" · ", messages);
        return RedirectToPage();
    }

    private static async Task TryAsync(List<string> messages, string label, Func<Task<string>> action)
    {
        try
        {
            messages.Add($"{label}: {await action()}");
        }
        catch (Exception ex)
        {
            messages.Add($"{label}: přeskočeno ({ex.Message})");
        }
    }
}
