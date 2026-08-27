using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
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
    ILogger<EmployeeSyncService>? logger = null)
{
    /// <summary>Po kolika záznamech se průběžně ukládá.</summary>
    private const int BatchSize = 500;

    public async Task<SyncResult> SyncAsync(string? userName, CancellationToken ct = default)
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

        foreach (var r in remote)
        {
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

        var result = new SyncResult(added, updated, deactivated);
        await audit.LogAsync(userName, "employees-synced", "Employee", null, result.ToString(), ct);
        return result;
    }
}
