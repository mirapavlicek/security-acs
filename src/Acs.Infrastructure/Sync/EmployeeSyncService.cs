using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Acs.Infrastructure.Sync;

/// <summary>
/// Import zaměstnanců z nakonfigurovaného zdroje (AD / MSSQL / API).
/// Ukládá po dávkách, aby velké domény (tisíce účtů) neběžely v jedné
/// obří transakci a šel sledovat průběh.
/// </summary>
public class EmployeeSyncService(
    AcsDbContext db, EmployeeSourceFactory sourceFactory, AuditService audit,
    ILogger<EmployeeSyncService>? logger = null, SettingsService? settings = null)
{
    /// <summary>Po kolika záznamech se průběžně ukládá.</summary>
    private const int BatchSize = 500;

    public async Task<EmployeeSyncResult> SyncAsync(string? userName, CancellationToken ct = default)
    {
        var source = await sourceFactory.CreateAsync(ct)
            ?? throw new InvalidOperationException("Zdroj zaměstnanců není nakonfigurován (Nastavení → Zdroj zaměstnanců).");

        var remote = await source.FetchAsync(ct);
        logger?.LogInformation("Synchronizace zaměstnanců: ze zdroje načteno {Count} záznamů.", remote.Count);

        var existing = await db.Employees
            .Where(e => e.ExternalId != null)
            .ToDictionaryAsync(e => e.ExternalId!, ct);

        int added = 0, updated = 0, deactivated = 0, processed = 0;
        var now = DateTime.UtcNow;

        foreach (var raw in remote)
        {
            // Ořezání platí pro všechny zdroje: AD vrací hodnoty s mezerami a MSSQL
            // u CHAR sloupců doplněné na pevnou délku. Osobní číslo se pak nespáruje
            // s kartou a v seznamech vypadá „divně“.
            var r = Trim(raw);

            if (++processed % BatchSize == 0)
            {
                await db.SaveChangesAsync(ct);
                logger?.LogInformation("Synchronizace zaměstnanců: zpracováno {Done}/{Total}.", processed, remote.Count);
            }

            if (existing.TryGetValue(r.ExternalId, out var local))
            {
                local.PersonalNumber = r.PersonalNumber;
                local.FirstName = r.FirstName;
                local.LastName = r.LastName;
                local.Email = r.Email;
                local.Department = r.Department;
                local.AdAccount = r.AdAccount;
                local.CardNumber = r.CardNumber;
                if (!local.ManagerManual)
                    local.ManagerAdAccount = r.ManagerExternalId ?? r.ManagerRaw;
                local.IsActive = true;
                local.LastSyncedAt = now;
                updated++;
            }
            else
            {
                db.Employees.Add(new Employee
                {
                    ExternalId = r.ExternalId,
                    PersonalNumber = r.PersonalNumber,
                    FirstName = r.FirstName,
                    LastName = r.LastName,
                    Email = r.Email,
                    Department = r.Department,
                    AdAccount = r.AdAccount,
                    CardNumber = r.CardNumber,
                    ManagerAdAccount = r.ManagerExternalId ?? r.ManagerRaw,
                    IsActive = true,
                    Source = RecordSource.Imported,
                    LastSyncedAt = now,
                });
                added++;
            }
        }

        var remoteIds = remote.Select(r => r.ExternalId).ToHashSet();
        foreach (var (externalId, local) in existing)
        {
            if (!remoteIds.Contains(externalId) && local.Source == RecordSource.Imported && local.IsActive)
            {
                local.IsActive = false;
                deactivated++;
            }
        }

        await db.SaveChangesAsync(ct);

        var managers = await LinkManagersAsync(remote, ct);

        // Automatické spárování AD účtů s importovanými zaměstnanci — bez toho
        // by uživatel neviděl „Moje přístupy“ a nešlo by za něj žádat.
        var unpaired = await db.Users.Where(u => !u.IsLocal && u.EmployeeId == null).ToListAsync(ct);
        if (unpaired.Count > 0)
        {
            var byAdAccount = await db.Employees
                .Where(e => e.AdAccount != null)
                .ToDictionaryAsync(e => e.AdAccount!, e => e.Id, StringComparer.OrdinalIgnoreCase, ct);
            var paired = 0;
            foreach (var user in unpaired)
            {
                if (byAdAccount.TryGetValue(user.UserName, out var employeeId))
                {
                    user.EmployeeId = employeeId;
                    paired++;
                }
            }

            if (paired > 0)
            {
                await db.SaveChangesAsync(ct);
                await audit.LogAsync(userName, "users-paired-to-employees", "AppUser", null,
                    $"spárováno {paired} účtů", ct);
            }
        }

        var result = new EmployeeSyncResult(added, updated, deactivated)
        {
            ManagersInSource = managers.InSource,
            ManagersLinked = managers.Linked,
            EmployeesTotal = remote.Count,
        };
        await audit.LogAsync(userName, "employees-synced", "Employee", null, result.ToString(), ct);
        if (settings is not null)
            await settings.SetAsync(SettingKeys.EmployeeLastManagerStats, result.ManagerSummary, userName, ct);
        return result;
    }

    /// <summary>
    /// Druhý průchod: nastaví <see cref="Employee.ManagerId"/> podle <c>ManagerExternalId</c>
    /// ze zdroje. Až po uložení všech záznamů — nadřízený může být v dávce později
    /// než podřízený a musí mít Id. Ručně zadaný nadřízený (<see cref="Employee.ManagerManual"/>)
    /// se nepřepisuje. Vrací statistiku: kolik záznamů nadřízeného ve zdroji má
    /// a kolik se podařilo spárovat — okamžitá odpověď, zda AD nadřízené vůbec vede.
    /// </summary>
    private async Task<(int InSource, int Linked)> LinkManagersAsync(
        IReadOnlyList<EmployeeRecord> remote, CancellationToken ct)
    {
        var byExternalId = await db.Employees
            .Where(e => e.ExternalId != null)
            .ToDictionaryAsync(e => e.ExternalId!, StringComparer.OrdinalIgnoreCase, ct);

        int inSource = 0, linked = 0;
        foreach (var raw in remote)
        {
            var externalId = raw.ExternalId.Trim();
            if (!byExternalId.TryGetValue(externalId, out var local) || local.ManagerManual)
                continue;

            if (!string.IsNullOrWhiteSpace(raw.ManagerRaw) || !string.IsNullOrWhiteSpace(raw.ManagerExternalId))
                inSource++;

            var managerExternalId = Clean(raw.ManagerExternalId);
            Employee? manager = null;
            if (managerExternalId is not null
                && byExternalId.TryGetValue(managerExternalId, out var found)
                && found.Id != local.Id)
            {
                manager = found;
            }

            if (manager is not null)
                linked++;

            var managerId = manager?.Id;
            if (local.ManagerId != managerId)
                local.ManagerId = managerId;
        }

        await db.SaveChangesAsync(ct);
        logger?.LogInformation(
            "Synchronizace zaměstnanců: nadřízený ve zdroji u {InSource} záznamů, spárováno {Linked}.",
            inSource, linked);
        return (inSource, linked);
    }

    /// <summary>Ořeže mezery a prázdné texty převede na null, ať se hodnoty párují.</summary>
    private static EmployeeRecord Trim(EmployeeRecord r) => r with
    {
        ExternalId = r.ExternalId.Trim(),
        PersonalNumber = Clean(r.PersonalNumber),
        FirstName = r.FirstName.Trim(),
        LastName = r.LastName.Trim(),
        Email = Clean(r.Email),
        Department = Clean(r.Department),
        AdAccount = Clean(r.AdAccount),
        CardNumber = Clean(r.CardNumber),
        ManagerExternalId = Clean(r.ManagerExternalId),
        ManagerRaw = Clean(r.ManagerRaw),
    };

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Výsledek synchronizace zaměstnanců včetně statistiky nadřízených.</summary>
public record EmployeeSyncResult(int Added, int Updated, int Deactivated)
    : SyncResult(Added, Updated, Deactivated)
{
    /// <summary>U kolika záznamů zdroj nadřízeného uvádí (atribut je vyplněn).</summary>
    public int ManagersInSource { get; init; }
    /// <summary>U kolika záznamů se nadřízený spároval s existujícím zaměstnancem.</summary>
    public int ManagersLinked { get; init; }
    public int EmployeesTotal { get; init; }

    public string ManagerSummary => ManagersInSource == 0
        ? $"zdroj neuvádí nadřízeného u žádného z {EmployeesTotal} zaměstnanců"
        : $"nadřízený nalezen u {ManagersLinked} z {EmployeesTotal} zaměstnanců (zdroj ho uvádí u {ManagersInSource})";

    public override string ToString() => $"{base.ToString()}; {ManagerSummary}";
}
