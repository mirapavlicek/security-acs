using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Workflow;

/// <summary>
/// Fronta realizace ICT: schválené žádosti o kamery / EZS čekají na technické zajištění.
/// Správce ICT po realizaci položku potvrdí (stav <see cref="RequestStatus.ManuallyConfirmed"/>
/// se u kamer / EZS zobrazuje jako „realizováno“), nebo ji s odůvodněním zamítne jako
/// technicky nerealizovatelnou.
/// </summary>
public class SecurityAdminService(AcsDbContext db, AuditService audit, INotificationService? notifier = null)
{
    private IQueryable<AccessRequestItem> Items => db.AccessRequestItems
        .Include(i => i.Request!).ThenInclude(r => r.TargetEmployee)
        .Include(i => i.Request!).ThenInclude(r => r.RequesterUser)
        .Include(i => i.SecurityRequest!).ThenInclude(s => s.Building)
        .Include(i => i.SecurityRequest!).ThenInclude(s => s.Floor)
        .Include(i => i.SecurityRequest!).ThenInclude(s => s.Room)
        .Include(i => i.SecurityRequest!).ThenInclude(s => s.OrgUnit)
        .Include(i => i.SecurityRequest!).ThenInclude(s => s.ImplementedByUser)
        .Where(i => i.SecurityRequestId != null);

    /// <summary>Schválené žádosti čekající na realizaci.</summary>
    public Task<List<AccessRequestItem>> GetQueueAsync(CancellationToken ct = default)
        => Items.Where(i => i.Status == RequestStatus.Approved)
            .OrderBy(i => i.Request!.CreatedAt)
            .ToListAsync(ct);

    /// <summary>Realizované žádosti (nejnovější první).</summary>
    public Task<List<AccessRequestItem>> GetImplementedAsync(string? search = null, int take = 200, CancellationToken ct = default)
    {
        var query = Items.Where(i => i.Status == RequestStatus.ManuallyConfirmed);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(i =>
                i.SecurityRequest!.Title.Contains(term)
                || (i.SecurityRequest.LocationText != null && i.SecurityRequest.LocationText.Contains(term))
                || i.Request!.TargetEmployee!.LastName.Contains(term)
                || i.Request.TargetEmployee.FirstName.Contains(term));
        }

        return query.OrderByDescending(i => i.SecurityRequest!.ImplementedAt).Take(take).ToListAsync(ct);
    }

    /// <summary>Potvrzení realizace správcem ICT.</summary>
    public async Task ImplementAsync(int itemId, int userId, string? note, string? userName, CancellationToken ct = default)
    {
        var item = await db.AccessRequestItems.Include(i => i.SecurityRequest)
            .FirstOrDefaultAsync(i => i.Id == itemId, ct)
            ?? throw new KeyNotFoundException("Položka žádosti nenalezena.");
        if (item.SecurityRequest is null)
            throw new InvalidOperationException("Položka není žádostí o kamery / EZS.");
        if (item.Status != RequestStatus.Approved)
            throw new InvalidOperationException("Položka nečeká na realizaci.");

        var now = DateTime.UtcNow;
        item.Status = RequestStatus.ManuallyConfirmed;
        item.PushedAt = now;
        item.PushResult = string.IsNullOrWhiteSpace(note) ? "realizováno ICT útvarem" : note.Trim();
        item.SecurityRequest.ImplementedAt = now;
        item.SecurityRequest.ImplementedByUserId = userId;
        item.SecurityRequest.ImplementationNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(userName, "security-request-implemented", "SecurityRequest",
            item.SecurityRequest.Id.ToString(), item.PushResult, ct);

        if (notifier is not null)
            await notifier.NotifyDecidedAsync(item.Id, ct);
    }

    /// <summary>Zamítnutí ve frontě realizace (technicky nerealizovatelné) — důvod je povinný.</summary>
    public async Task RejectAsync(int itemId, int userId, string reason, string? userName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("Uveďte důvod zamítnutí.");

        var item = await db.AccessRequestItems.Include(i => i.SecurityRequest).Include(i => i.Decisions)
            .FirstOrDefaultAsync(i => i.Id == itemId, ct)
            ?? throw new KeyNotFoundException("Položka žádosti nenalezena.");
        if (item.SecurityRequest is null)
            throw new InvalidOperationException("Položka není žádostí o kamery / EZS.");
        if (item.Status != RequestStatus.Approved)
            throw new InvalidOperationException("Položka nečeká na realizaci.");

        item.Decisions.Add(new ApprovalDecision
        {
            LevelOrder = 0,
            ApproverUserId = userId,
            Approved = false,
            Comment = $"[realizace ICT] {reason.Trim()}",
        });
        item.Status = RequestStatus.Rejected;
        item.DecidedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(userName, "security-request-rejected", "SecurityRequest",
            item.SecurityRequest.Id.ToString(), reason.Trim(), ct);

        if (notifier is not null)
            await notifier.NotifyDecidedAsync(item.Id, ct);
    }
}
