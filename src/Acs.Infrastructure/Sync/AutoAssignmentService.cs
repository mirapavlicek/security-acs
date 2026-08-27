using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Sync;

public record AutoAssignResult(int Created)
{
    public override string ToString() => $"založeno {Created} automatických přístupů";
}

/// <summary>
/// Automatické zařazení: zaměstnanec s oddělením odpovídajícím pravidlu
/// dostane <b>předschválený</b> přístup ke skupině čteček — položka jde rovnou
/// do fronty správce karet (např. nástup na chirurgii → základní přístup
/// skupiny Chirurgie, včetně vnořených skupin).
/// </summary>
public class AutoAssignmentService(AcsDbContext db, AuditService audit)
{
    public const string SystemUserName = AccessSyncService.SystemUserName;

    public async Task<AutoAssignResult> RunAsync(string? userName, CancellationToken ct = default)
    {
        var rules = await db.AutoAssignmentRules
            .Include(r => r.ReaderGroup)
            .Where(r => r.IsActive && r.ReaderGroup!.IsActive)
            .ToListAsync(ct);
        if (rules.Count == 0)
            return new AutoAssignResult(0);

        var employees = await db.Employees
            .Where(e => e.IsActive && e.Department != null)
            .ToListAsync(ct);

        // Existující položky (běžící/aktivní) na skupiny, ať nezakládáme duplicitně.
        var groupIds = rules.Select(r => r.ReaderGroupId).Distinct().ToList();
        var existing = await db.AccessRequestItems
            .Where(i => i.ReaderGroupId != null && groupIds.Contains(i.ReaderGroupId.Value)
                        && i.Request!.Kind == RequestKind.Grant
                        && (i.Status == RequestStatus.Pending
                            || i.Status == RequestStatus.Approved
                            || i.Status == RequestStatus.PushedToWinPak
                            || i.Status == RequestStatus.ManuallyConfirmed))
            .Select(i => new { i.Request!.TargetEmployeeId, GroupId = i.ReaderGroupId!.Value })
            .ToListAsync(ct);
        var existingSet = existing.Select(x => (x.TargetEmployeeId, x.GroupId)).ToHashSet();

        var systemUser = await GetOrCreateSystemUserAsync(ct);
        var created = 0;

        foreach (var rule in rules)
        {
            foreach (var employee in employees.Where(e =>
                         string.Equals(e.Department, rule.Department, StringComparison.OrdinalIgnoreCase)
                         && !existingSet.Contains((e.Id, rule.ReaderGroupId))))
            {
                db.AccessRequests.Add(new AccessRequest
                {
                    Kind = RequestKind.Grant,
                    RequesterUserId = systemUser.Id,
                    TargetEmployeeId = employee.Id,
                    Justification = $"automatické zařazení dle oddělení „{rule.Department}“",
                    Items =
                    [
                        new AccessRequestItem
                        {
                            ReaderGroupId = rule.ReaderGroupId,
                            Status = RequestStatus.Approved, // předschváleno → fronta správce karet
                            DecidedAt = DateTime.UtcNow,
                        },
                    ],
                });
                existingSet.Add((employee.Id, rule.ReaderGroupId));
                created++;
            }
        }

        await db.SaveChangesAsync(ct);
        var result = new AutoAssignResult(created);
        if (created > 0)
            await audit.LogAsync(userName, "auto-assignment-run", "AccessRequest", null, result.ToString(), ct);
        return result;
    }

    private async Task<AppUser> GetOrCreateSystemUserAsync(CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == SystemUserName, ct);
        if (user is null)
        {
            user = new AppUser
            {
                UserName = SystemUserName,
                DisplayName = "Synchronizace WIN-PAK",
                IsLocal = true,
                PasswordHash = Auth.PasswordHasher.Hash(Guid.NewGuid().ToString("N")),
                IsActive = false,
                Roles = AppRole.None,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
        }

        return user;
    }
}
