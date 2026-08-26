using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Sync;

/// <summary>Import zaměstnanců z nakonfigurovaného zdroje (MSSQL/API).</summary>
public class EmployeeSyncService(AcsDbContext db, EmployeeSourceFactory sourceFactory, AuditService audit)
{
    public async Task<SyncResult> SyncAsync(string? userName, CancellationToken ct = default)
    {
        var source = await sourceFactory.CreateAsync(ct)
            ?? throw new InvalidOperationException("Zdroj zaměstnanců není nakonfigurován (Nastavení → Zdroj zaměstnanců).");

        var remote = await source.FetchAsync(ct);
        var existing = await db.Employees
            .Where(e => e.ExternalId != null)
            .ToDictionaryAsync(e => e.ExternalId!, ct);

        int added = 0, updated = 0, deactivated = 0;
        var now = DateTime.UtcNow;

        foreach (var r in remote)
        {
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
        var result = new SyncResult(added, updated, deactivated);
        await audit.LogAsync(userName, "employees-synced", "Employee", null, result.ToString(), ct);
        return result;
    }
}
