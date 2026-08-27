using System.Security.Claims;
using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Workflow;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Notifications;

/// <summary>Počty věcí, které čekají na zásah přihlášeného uživatele.</summary>
public record AttentionCounts(int PendingApprovals, int CardQueue, int MyPending)
{
    public int Total => PendingApprovals + CardQueue;
}

/// <summary>
/// Spočítá, co na uživatele čeká — pro červené indikátory v navigaci a na dlaždicích.
/// Výsledek se drží po dobu požadavku (layout i stránka se ptají opakovaně).
/// </summary>
public class AttentionService(AcsDbContext db, RequestWorkflowService workflow)
{
    private AttentionCounts? _cached;

    public async Task<AttentionCounts> GetAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (_cached is not null)
            return _cached;

        if (user.Identity?.IsAuthenticated != true
            || !int.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
        {
            return _cached = new AttentionCounts(0, 0, 0);
        }

        var isAdmin = user.IsInRole(nameof(AppRole.Admin));
        var pendingApprovals = (await workflow.GetPendingForApproverAsync(userId, isAdmin, ct)).Count;

        var cardQueue = isAdmin || user.IsInRole(nameof(AppRole.CardAdmin))
            ? await db.AccessRequestItems.CountAsync(i => i.Status == RequestStatus.Approved, ct)
            : 0;

        // Vlastní žádosti, které ještě nejsou vyřízené (informativní, bez červené).
        var myPending = await db.AccessRequestItems.CountAsync(
            i => i.Request!.RequesterUserId == userId
                 && (i.Status == RequestStatus.Pending || i.Status == RequestStatus.Approved), ct);

        return _cached = new AttentionCounts(pendingApprovals, cardQueue, myPending);
    }
}
