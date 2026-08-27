using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.WinPak;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Automation;

public enum HealthSeverity { Info, Warning, Problem }

public record HealthItem(HealthSeverity Severity, string Title, string Detail, string? Hint = null);

/// <summary>
/// Self-diagnostika: hledá konfigurační mezery, které tiše brzdí automatiku
/// (chybějící mapování, nenastavené zdroje, čekající fronta…). Cílem je, aby
/// administrátor nemusel chyby hledat v logu.
/// </summary>
public class HealthCheckService(AcsDbContext db, SettingsService settings, WinPakClient winPak)
{
    public async Task<List<HealthItem>> RunAsync(CancellationToken ct = default)
    {
        var items = new List<HealthItem>();

        // --- WIN-PAK konektor ---
        var baseUrl = await settings.GetAsync(SettingKeys.WinPakBaseUrl, ct);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            items.Add(new HealthItem(HealthSeverity.Problem, "WIN-PAK konektor není nakonfigurován",
                "Bez konektoru nelze načítat čtečky ani předávat přístupy.",
                "Nastavení → WIN-PAK konektor"));
        }
        else
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(4));
                var info = await winPak.GetInfoAsync(cts.Token);
                if (info is null)
                {
                    items.Add(new HealthItem(HealthSeverity.Problem, "WIN-PAK konektor neodpovídá",
                        "Synchronizace i předávání přístupů jsou pozastaveny.", "Nastavení → WIN-PAK konektor"));
                }
                else if (!info.SupportsWrite)
                {
                    items.Add(new HealthItem(HealthSeverity.Warning, "WIN-PAK konektor je jen pro čtení",
                        $"Režim {info.ProviderMode} neumí zápis — přístupy musí správce karet zadávat ručně.",
                        "Konektor: přepnout na režim Sdk (vyžaduje licenci SRVWPPAPI)"));
                }
            }
            catch (Exception ex)
            {
                items.Add(new HealthItem(HealthSeverity.Problem, "WIN-PAK konektor je nedostupný",
                    ex.Message, "Nastavení → WIN-PAK konektor"));
            }
        }

        // --- Zdroje dat ---
        var employeeMode = await settings.GetAsync(SettingKeys.EmployeeSourceMode, ct);
        if (string.IsNullOrWhiteSpace(employeeMode) || employeeMode.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new HealthItem(HealthSeverity.Warning, "Zdroj zaměstnanců není nastaven",
                "Zaměstnance je nutné zakládat ručně; nefunguje automatické zařazení ani offboarding.",
                "Nastavení → Zdroj zaměstnanců (režim Ad)"));
        }
        else if (employeeMode.Equals("Ad", StringComparison.OrdinalIgnoreCase)
                 && string.IsNullOrWhiteSpace(await settings.GetAsync(SettingKeys.LdapBindUser, ct)))
        {
            items.Add(new HealthItem(HealthSeverity.Problem, "Chybí servisní účet pro AD",
                "Bez bind účtu nelze načítat zaměstnance z Active Directory.",
                "Nastavení → Active Directory"));
        }

        if (string.IsNullOrWhiteSpace(await settings.GetAsync(SettingKeys.CardsMssqlQuery, ct)))
        {
            items.Add(new HealthItem(HealthSeverity.Warning, "Není nastavena synchronizace karet",
                "Čísla karet a WIN-PAK card holder id se nebudou doplňovat ze SQL.",
                "Nastavení → Karty (MSSQL)"));
        }

        if (string.IsNullOrWhiteSpace(await settings.GetAsync(SettingKeys.SmtpHost, ct)))
        {
            items.Add(new HealthItem(HealthSeverity.Warning, "Není nastaven SMTP server",
                "Neodesílají se notifikace schvalovatelům, připomínky ani eskalace.",
                "Nastavení → Notifikace (SMTP)"));
        }

        // --- Data blokující předání do WIN-PAK ---
        var readersWithoutAccessLevel = await db.Readers
            .CountAsync(r => r.IsActive && r.AccessLevelExternalId == null, ct);
        if (readersWithoutAccessLevel > 0)
        {
            items.Add(new HealthItem(HealthSeverity.Warning, "Čtečky bez WIN-PAK access levelu",
                $"{readersWithoutAccessLevel} aktivních čteček nelze předat do WIN-PAK.",
                "Čtečky → editace → WIN-PAK access level"));
        }

        var employeesWithoutHolder = await db.Employees
            .CountAsync(e => e.IsActive && e.WinPakCardHolderId == null, ct);
        if (employeesWithoutHolder > 0)
        {
            items.Add(new HealthItem(HealthSeverity.Warning, "Zaměstnanci bez card holder id",
                $"{employeesWithoutHolder} aktivních zaměstnanců nelze zapsat do WIN-PAK.",
                "Synchronizace karet ze SQL, nebo ruční doplnění"));
        }

        var groupsWithoutMatrix = await db.ReaderGroups
            .CountAsync(g => g.IsActive && g.ApprovalMatrixId == null, ct);
        if (groupsWithoutMatrix > 0)
        {
            items.Add(new HealthItem(HealthSeverity.Info, "Skupiny bez schvalovací matice",
                $"{groupsWithoutMatrix} skupin musí schvalovat administrátor ručně.",
                "Skupiny → přiřadit matici"));
        }

        var emptyMatrices = await db.ApprovalMatrices
            .CountAsync(m => m.IsActive && !m.Levels.Any(), ct);
        if (emptyMatrices > 0)
        {
            items.Add(new HealthItem(HealthSeverity.Warning, "Matice bez úrovní",
                $"{emptyMatrices} aktivních matic nemá žádnou úroveň — chovají se jako bez matice.",
                "Matice → přidat úroveň a schvalovatele"));
        }

        var levelsWithoutApprovers = await db.ApprovalLevels
            .CountAsync(l => !l.Approvers.Any(), ct);
        if (levelsWithoutApprovers > 0)
        {
            items.Add(new HealthItem(HealthSeverity.Problem, "Úrovně bez schvalovatelů",
                $"{levelsWithoutApprovers} úrovní nemá schvalovatele — žádosti by v nich uvázly.",
                "Matice → doplnit schvalovatele"));
        }

        // --- Provozní stav ---
        var queueCount = await db.AccessRequestItems.CountAsync(i => i.Status == RequestStatus.Approved, ct);
        if (queueCount > 0)
        {
            items.Add(new HealthItem(HealthSeverity.Info, "Fronta správce karet",
                $"{queueCount} schválených položek čeká na zadání do WIN-PAK.",
                "Fronta karet (nebo zapnout automatické předávání)"));
        }

        var oldestPending = await db.AccessRequestItems
            .Where(i => i.Status == RequestStatus.Pending)
            .OrderBy(i => i.Request!.CreatedAt)
            .Select(i => (DateTime?)i.Request!.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (oldestPending is not null && DateTime.UtcNow - oldestPending.Value > TimeSpan.FromDays(7))
        {
            items.Add(new HealthItem(HealthSeverity.Warning, "Dlouho čekající schválení",
                $"Nejstarší žádost čeká {(int)(DateTime.UtcNow - oldestPending.Value).TotalDays} dní.",
                "Žádosti → zkontrolovat schvalovatele a zástupy"));
        }

        var usersWithoutEmployee = await db.Users
            .CountAsync(u => u.IsActive && !u.IsLocal && u.EmployeeId == null, ct);
        if (usersWithoutEmployee > 0)
        {
            items.Add(new HealthItem(HealthSeverity.Info, "Uživatelé bez vazby na zaměstnance",
                $"{usersWithoutEmployee} AD účtů nemá spárovaného zaměstnance — nevidí „Moje přístupy“.",
                "Zaměstnanci → doplnit AD účet (páruje se automaticky při synchronizaci)"));
        }

        return items;
    }
}
