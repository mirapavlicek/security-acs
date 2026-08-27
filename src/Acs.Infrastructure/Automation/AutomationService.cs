using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Notifications;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.Sync;
using Acs.Infrastructure.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Acs.Infrastructure.Automation;

public record AutomationResult(
    int OffboardedAccesses,
    int DepartmentChangeRevocations,
    int ExpiredAccesses,
    int Reminders,
    int Escalations,
    int AutoPushed,
    int AutoPushFailed)
{
    public bool AnythingDone => OffboardedAccesses + DepartmentChangeRevocations + ExpiredAccesses
        + Reminders + Escalations + AutoPushed + AutoPushFailed > 0;

    public override string ToString() =>
        $"offboarding {OffboardedAccesses}, změna oddělení {DepartmentChangeRevocations}, "
        + $"expirace {ExpiredAccesses}, připomínky {Reminders}, eskalace {Escalations}, "
        + $"automaticky předáno {AutoPushed}" + (AutoPushFailed > 0 ? $", selhalo {AutoPushFailed}" : "");
}

/// <summary>
/// Automatizace, které jinak musí dělat člověk ručně:
/// <list type="bullet">
///   <item><b>Offboarding</b> — zaměstnanec zmizel z AD / byl deaktivován → automaticky
///     se založí předschválené odebrání všech jeho aktivních přístupů.</item>
///   <item><b>Změna oddělení</b> — přístup získaný automatickým zařazením se při přesunu
///     na jiné oddělení odebere (nové zařazení doplní <see cref="AutoAssignmentService"/>).</item>
///   <item><b>Expirace</b> — po uplynutí platnosti žádosti se přístup automaticky odebere.</item>
///   <item><b>Připomínky a eskalace</b> — dlouho čekající schválení připomene schvalovatelům
///     a po delší době eskaluje na administrátory.</item>
///   <item><b>Automatické předání do WIN-PAK</b> — schválené položky se předají samy,
///     správce karet nemusí nic odklikávat (lze vypnout).</item>
/// </list>
/// Všechny části lze samostatně zapnout/vypnout v Nastavení → Automatizace.
/// </summary>
public class AutomationService(
    AcsDbContext db,
    SettingsService settings,
    AuditService audit,
    CardAdminService cardAdmin,
    ILogger<AutomationService> logger,
    INotificationService? notifier = null)
{
    public async Task<AutomationResult> RunAsync(string? userName, CancellationToken ct = default)
    {
        var systemUser = await GetSystemUserAsync(ct);

        var offboarded = await settings.GetBoolAsync(SettingKeys.AutoOffboardingEnabled, true, ct)
            ? await RunOffboardingAsync(systemUser, ct) : 0;
        var deptChanged = await settings.GetBoolAsync(SettingKeys.AutoDepartmentChangeEnabled, true, ct)
            ? await RunDepartmentChangeAsync(systemUser, ct) : 0;
        var expired = await settings.GetBoolAsync(SettingKeys.AutoExpirationEnabled, true, ct)
            ? await RunExpirationAsync(systemUser, ct) : 0;

        var (reminders, escalations) = await settings.GetBoolAsync(SettingKeys.AutoRemindersEnabled, true, ct)
            ? await RunRemindersAsync(ct) : (0, 0);

        var (pushed, failed) = await settings.GetBoolAsync(SettingKeys.AutoPushEnabled, false, ct)
            ? await RunAutoPushAsync(ct) : (0, 0);

        var result = new AutomationResult(offboarded, deptChanged, expired, reminders, escalations, pushed, failed);
        if (result.AnythingDone)
            await audit.LogAsync(userName ?? "system", "automation-run", null, null, result.ToString(), ct);
        return result;
    }

    // ---------- Offboarding ----------

    private async Task<int> RunOffboardingAsync(AppUser systemUser, CancellationToken ct)
    {
        var inactiveIds = await db.Employees.Where(e => !e.IsActive).Select(e => e.Id).ToListAsync(ct);
        if (inactiveIds.Count == 0)
            return 0;

        var items = await ActiveGrantItemsAsync(inactiveIds, ct);
        return await CreateRevocationsAsync(items, systemUser,
            "offboarding — zaměstnanec je neaktivní (odešel / zmizel z AD)", "automation-offboarding", ct);
    }

    // ---------- Změna oddělení ----------

    private async Task<int> RunDepartmentChangeAsync(AppUser systemUser, CancellationToken ct)
    {
        // Přístupy udělené automatickým zařazením, jejichž oddělení už zaměstnanci nesedí.
        var candidates = await db.AccessRequestItems
            .Include(i => i.Request!).ThenInclude(r => r.TargetEmployee)
            .Where(i => i.Request!.Kind == RequestKind.Grant
                        && i.Request.AutoAssignedDepartment != null
                        && i.Request.TargetEmployee!.IsActive
                        && (i.Status == RequestStatus.Pending
                            || i.Status == RequestStatus.Approved
                            || i.Status == RequestStatus.PushedToWinPak
                            || i.Status == RequestStatus.ManuallyConfirmed))
            .ToListAsync(ct);

        var stale = candidates
            .Where(i => !string.Equals(i.Request!.TargetEmployee!.Department,
                                       i.Request.AutoAssignedDepartment, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return await CreateRevocationsAsync(stale, systemUser,
            "změna oddělení — automaticky přidělený přístup už neodpovídá zařazení",
            "automation-department-change", ct);
    }

    // ---------- Expirace ----------

    private async Task<int> RunExpirationAsync(AppUser systemUser, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var expired = await db.AccessRequestItems
            .Include(i => i.Request)
            .Where(i => i.Request!.Kind == RequestKind.Grant
                        && i.Request.ValidUntil != null && i.Request.ValidUntil < now
                        && (i.Status == RequestStatus.Pending
                            || i.Status == RequestStatus.Approved
                            || i.Status == RequestStatus.PushedToWinPak
                            || i.Status == RequestStatus.ManuallyConfirmed))
            .ToListAsync(ct);

        return await CreateRevocationsAsync(expired, systemUser,
            "uplynula platnost přístupu", "automation-expiration", ct);
    }

    // ---------- Připomínky a eskalace ----------

    private async Task<(int Reminders, int Escalations)> RunRemindersAsync(CancellationToken ct)
    {
        var reminderDays = Math.Max(1, await settings.GetIntAsync(SettingKeys.AutoReminderAfterDays, 3, ct));
        var escalationDays = Math.Max(reminderDays, await settings.GetIntAsync(SettingKeys.AutoEscalationAfterDays, 7, ct));
        var now = DateTime.UtcNow;

        var pending = await db.AccessRequestItems
            .Include(i => i.Request)
            .Where(i => i.Status == RequestStatus.Pending)
            .ToListAsync(ct);

        int reminders = 0, escalations = 0;
        foreach (var item in pending)
        {
            var waitingSince = item.LastReminderAt ?? item.Request!.CreatedAt;
            var age = now - item.Request!.CreatedAt;
            if (now - waitingSince < TimeSpan.FromDays(reminderDays))
                continue;

            if (notifier is not null)
                await notifier.NotifyPendingAsync(item.Id, ct);
            item.LastReminderAt = now;
            reminders++;

            if (age >= TimeSpan.FromDays(escalationDays) && notifier is not null)
            {
                await notifier.NotifyEscalationAsync(item.Id, (int)age.TotalDays, ct);
                escalations++;
            }
        }

        if (reminders > 0)
            await db.SaveChangesAsync(ct);
        return (reminders, escalations);
    }

    // ---------- Automatické předání do WIN-PAK ----------

    private async Task<(int Pushed, int Failed)> RunAutoPushAsync(CancellationToken ct)
    {
        var queue = await cardAdmin.GetQueueAsync(ct);
        if (queue.Count == 0)
            return (0, 0);

        int pushed = 0, failed = 0;
        foreach (var item in queue)
        {
            try
            {
                await cardAdmin.PushAsync(item.Id, "automation", ct);
                pushed++;
            }
            catch (Exception ex)
            {
                // Chybějící mapování / nedostupný konektor — položka zůstane ve frontě
                // pro správce karet, automatika nesmí spadnout.
                logger.LogWarning(ex, "Automatické předání položky {ItemId} do WIN-PAK selhalo.", item.Id);
                failed++;
            }
        }

        return (pushed, failed);
    }

    // ---------- Pomocné ----------

    private Task<List<AccessRequestItem>> ActiveGrantItemsAsync(List<int> employeeIds, CancellationToken ct)
        => db.AccessRequestItems
            .Include(i => i.Request)
            .Where(i => i.Request!.Kind == RequestKind.Grant
                        && employeeIds.Contains(i.Request.TargetEmployeeId)
                        && (i.Status == RequestStatus.Pending
                            || i.Status == RequestStatus.Approved
                            || i.Status == RequestStatus.PushedToWinPak
                            || i.Status == RequestStatus.ManuallyConfirmed))
            .ToListAsync(ct);

    /// <summary>
    /// Založí předschválené revokace pro dané položky (jdou rovnou do fronty správce karet).
    /// Položky, které ještě nebyly zapsané do WIN-PAK, se rovnou zruší — není co odebírat.
    /// </summary>
    private async Task<int> CreateRevocationsAsync(
        List<AccessRequestItem> items, AppUser systemUser, string reason, string auditAction, CancellationToken ct)
    {
        if (items.Count == 0)
            return 0;

        var created = 0;
        foreach (var group in items.GroupBy(i => i.Request!.TargetEmployeeId))
        {
            var revokeItems = new List<AccessRequestItem>();
            foreach (var item in group)
            {
                if (item.Status is RequestStatus.Pending or RequestStatus.Approved)
                {
                    // Ještě není ve WIN-PAK → stačí zrušit běžící žádost.
                    item.Status = RequestStatus.Cancelled;
                    item.DecidedAt = DateTime.UtcNow;
                    item.PushResult = reason;
                    created++;
                    continue;
                }

                // Aktivní přístup ve WIN-PAK → předschválená revokace do fronty správce karet.
                if (await HasPendingRevocationAsync(group.Key, item, ct))
                    continue;

                revokeItems.Add(new AccessRequestItem
                {
                    ReaderId = item.ReaderId,
                    ReaderGroupId = item.ReaderGroupId,
                    Status = RequestStatus.Approved,
                    DecidedAt = DateTime.UtcNow,
                });
                created++;
            }

            if (revokeItems.Count > 0)
            {
                db.AccessRequests.Add(new AccessRequest
                {
                    Kind = RequestKind.Revoke,
                    RequesterUserId = systemUser.Id,
                    TargetEmployeeId = group.Key,
                    Justification = reason,
                    Items = revokeItems,
                });
            }
        }

        if (created > 0)
        {
            await db.SaveChangesAsync(ct);
            await audit.LogAsync("system", auditAction, "AccessRequestItem", null,
                $"{created} položek — {reason}", ct);
        }

        return created;
    }

    private Task<bool> HasPendingRevocationAsync(int employeeId, AccessRequestItem source, CancellationToken ct)
        => db.AccessRequestItems.AnyAsync(i =>
            i.Request!.Kind == RequestKind.Revoke
            && i.Request.TargetEmployeeId == employeeId
            && (source.ReaderId != null ? i.ReaderId == source.ReaderId : i.ReaderGroupId == source.ReaderGroupId)
            && (i.Status == RequestStatus.Pending || i.Status == RequestStatus.Approved), ct);

    private async Task<AppUser> GetSystemUserAsync(CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == AccessSyncService.SystemUserName, ct);
        if (user is null)
        {
            user = new AppUser
            {
                UserName = AccessSyncService.SystemUserName,
                DisplayName = "Automatizace ACS",
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
