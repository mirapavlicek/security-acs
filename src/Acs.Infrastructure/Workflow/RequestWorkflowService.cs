using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Workflow;

/// <summary>
/// Jádro schvalovacího workflow:
/// <list type="bullet">
///   <item>podání žádosti s automatickým doplněním řetězce čteček (uzávěr závislostí),</item>
///   <item>průchod položek úrovněmi matice (režimy kterýkoli / všichni / N-z-M),</item>
///   <item>zástupy (deputy) — rozhodování za jiného schvalovatele v daném období,</item>
///   <item>zamítnutí kdykoli ukončí položku.</item>
/// </list>
/// Bezpečnostní pravidla:
/// <list type="bullet">
///   <item>žádost pro jiného zaměstnance smí podat jen uživatel s oprávněním
///     (Admin / CardAdmin / CatalogManager); běžný uživatel jen sám za sebe,</item>
///   <item>čtečka bez aktivní matice se <b>neschvaluje automaticky</b> — vyžaduje
///     rozhodnutí administrátora (žádný přístup neobejde lidské schválení).</item>
/// </list>
/// </summary>
public class RequestWorkflowService(
    AcsDbContext db, AuditService audit,
    INotificationService? notifier = null, ReaderGroupService? groups = null)
{
    private ReaderGroupService Groups => groups ?? new ReaderGroupService(db);

    // ---------- Podání žádosti ----------

    /// <summary>
    /// Vrátí id čteček rozšířené o všechny vyžadované čtečky (tranzitivně):
    /// <list type="bullet">
    ///   <item>ruční závislosti (graf „čtečka vyžaduje čtečku“),</item>
    ///   <item><b>chodbový řetěz</b> — čtečka v místnosti automaticky vyžaduje čtečky
    ///     chodby, ze které se do místnosti vchází, a čtečky všech nadřazených chodb
    ///     v řetězu (chodby se mohou řetězit i napříč patry).</item>
    /// </list>
    /// </summary>
    public async Task<HashSet<int>> ExpandWithDependenciesAsync(IEnumerable<int> readerIds, CancellationToken ct = default)
    {
        var edges = await db.ReaderDependencies
            .Select(d => new { d.ReaderId, d.RequiresReaderId })
            .ToListAsync(ct);
        var graph = edges.GroupBy(e => e.ReaderId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.RequiresReaderId).ToList());

        // Chodbové mapy: čtečka → výchozí chodba (přímá nebo přes místnost),
        // chodba → rodič, chodba → čtečky na ní.
        var readerCorridors = await db.Readers
            .Select(r => new { r.Id, CorridorId = r.CorridorId ?? (r.Room != null ? r.Room.CorridorId : null) })
            .Where(x => x.CorridorId != null)
            .ToDictionaryAsync(x => x.Id, x => x.CorridorId!.Value, ct);
        var corridorParents = await db.Corridors
            .Where(c => c.ParentCorridorId != null)
            .ToDictionaryAsync(c => c.Id, c => c.ParentCorridorId!.Value, ct);
        var corridorReaders = (await db.Readers
                .Where(r => r.CorridorId != null && r.IsActive)
                .Select(r => new { r.Id, CorridorId = r.CorridorId!.Value })
                .ToListAsync(ct))
            .GroupBy(x => x.CorridorId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());

        var result = new HashSet<int>();
        var stack = new Stack<int>(readerIds);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!result.Add(current))
                continue;

            // ruční závislosti
            if (graph.TryGetValue(current, out var required))
            {
                foreach (var r in required)
                    stack.Push(r);
            }

            // chodbový řetěz: chodba čtečky (pro čtečku na chodbě je to rodičovská
            // chodba — vlastní chodbu už pokrývá sama) a všichni předci
            if (readerCorridors.TryGetValue(current, out var startCorridor))
            {
                var corridorId = corridorReaders.TryGetValue(startCorridor, out var own) && own.Contains(current)
                    ? (corridorParents.TryGetValue(startCorridor, out var p) ? p : (int?)null)
                    : startCorridor;

                var visitedCorridors = new HashSet<int>();
                while (corridorId is not null && visitedCorridors.Add(corridorId.Value))
                {
                    if (corridorReaders.TryGetValue(corridorId.Value, out var onCorridor))
                    {
                        foreach (var r in onCorridor)
                            stack.Push(r);
                    }

                    corridorId = corridorParents.TryGetValue(corridorId.Value, out var parent)
                        ? parent
                        : null;
                }
            }
        }

        return result;
    }

    /// <param name="requesterCanActForOthers">
    /// true pro uživatele s oprávněním podat žádost i za jiné zaměstnance
    /// (Admin / CardAdmin / CatalogManager). Pro běžného uživatele false —
    /// pak smí žádat jen za zaměstnance navázaného na jeho účet.
    /// Systémové volání (zpětná synchronizace) používá <c>true</c>.
    /// </param>
    public async Task<AccessRequest> CreateRequestAsync(
        int requesterUserId, int targetEmployeeId, IReadOnlyCollection<int> readerIds,
        string? justification, RequestKind kind = RequestKind.Grant,
        bool requesterCanActForOthers = false, IReadOnlyCollection<int>? groupIds = null,
        CancellationToken ct = default)
    {
        groupIds ??= [];
        if (readerIds.Count == 0 && groupIds.Count == 0)
            throw new InvalidOperationException("Žádost musí obsahovat alespoň jednu čtečku nebo skupinu.");

        // Autorizace: kdo smí žádat za koho.
        if (!requesterCanActForOthers)
        {
            var ownEmployeeId = await db.Users
                .Where(u => u.Id == requesterUserId)
                .Select(u => u.EmployeeId)
                .FirstOrDefaultAsync(ct);
            if (ownEmployeeId is null || ownEmployeeId.Value != targetEmployeeId)
                throw new UnauthorizedAccessException(
                    "Nemáte oprávnění podat žádost za jiného zaměstnance.");
        }

        var allIds = kind == RequestKind.Grant
            ? await ExpandWithDependenciesAsync(readerIds, ct)
            : [.. readerIds]; // u revokace se řetězec nedoplňuje

        var readers = await db.Readers
            .Include(r => r.ApprovalMatrix!).ThenInclude(m => m.Levels)
            .Where(r => allIds.Contains(r.Id))
            .ToListAsync(ct);

        // Duplicitní položky: čtečky/skupiny, na které už cílový zaměstnanec má běžící/aktivní žádost.
        var duplicateReaderIds = await db.AccessRequestItems
            .Where(i => i.Request!.TargetEmployeeId == targetEmployeeId
                        && i.Request.Kind == RequestKind.Grant
                        && i.ReaderId != null && allIds.Contains(i.ReaderId.Value)
                        && (i.Status == RequestStatus.Pending
                            || i.Status == RequestStatus.Approved
                            || i.Status == RequestStatus.PushedToWinPak
                            || i.Status == RequestStatus.ManuallyConfirmed))
            .Select(i => i.ReaderId!.Value)
            .ToListAsync(ct);
        var skip = kind == RequestKind.Grant ? duplicateReaderIds.ToHashSet() : [];

        var duplicateGroupIds = await db.AccessRequestItems
            .Where(i => i.Request!.TargetEmployeeId == targetEmployeeId
                        && i.Request.Kind == RequestKind.Grant
                        && i.ReaderGroupId != null && groupIds.Contains(i.ReaderGroupId.Value)
                        && (i.Status == RequestStatus.Pending
                            || i.Status == RequestStatus.Approved
                            || i.Status == RequestStatus.PushedToWinPak
                            || i.Status == RequestStatus.ManuallyConfirmed))
            .Select(i => i.ReaderGroupId!.Value)
            .ToListAsync(ct);
        var skipGroups = kind == RequestKind.Grant ? duplicateGroupIds.ToHashSet() : [];

        var explicitIds = readerIds.ToHashSet();
        var request = new AccessRequest
        {
            Kind = kind,
            RequesterUserId = requesterUserId,
            TargetEmployeeId = targetEmployeeId,
            Justification = justification,
        };

        foreach (var reader in readers.Where(r => !skip.Contains(r.Id)))
        {
            var matrix = reader.ApprovalMatrix is { IsActive: true, Levels.Count: > 0 }
                ? reader.ApprovalMatrix
                : null;

            // Čtečka bez matice se NESCHVALUJE automaticky — zůstává Pending
            // a smí ji schválit pouze administrátor (viz GetPendingForApproverAsync).
            request.Items.Add(new AccessRequestItem
            {
                ReaderId = reader.Id,
                AutoAdded = !explicitIds.Contains(reader.Id),
                MatrixId = matrix?.Id,
                Status = RequestStatus.Pending,
                CurrentLevelOrder = matrix?.Levels.Min(l => l.Order) ?? 0,
            });
        }

        // Skupinové položky: řetěz matic = matice skupiny + matic nadřazených skupin.
        var levelCache = new Dictionary<int, int>(); // matrixId -> první úroveň
        foreach (var groupId in groupIds.Where(g => !skipGroups.Contains(g)))
        {
            var chain = await Groups.GetMatrixChainAsync(groupId, ct);
            var firstMatrixId = chain.Count > 0 ? chain[0] : (int?)null;
            int firstLevel = 0;
            if (firstMatrixId is not null)
            {
                if (!levelCache.TryGetValue(firstMatrixId.Value, out firstLevel))
                {
                    firstLevel = await db.ApprovalLevels
                        .Where(l => l.MatrixId == firstMatrixId.Value)
                        .MinAsync(l => (int?)l.Order, ct) ?? 0;
                    levelCache[firstMatrixId.Value] = firstLevel;
                }

                if (firstLevel == 0)
                    firstMatrixId = null; // matice bez úrovní → jako bez matice (schvaluje admin)
            }

            request.Items.Add(new AccessRequestItem
            {
                ReaderGroupId = groupId,
                MatrixId = firstMatrixId,
                Status = RequestStatus.Pending,
                CurrentStageOrder = 1,
                CurrentLevelOrder = firstLevel,
                Stages = firstMatrixId is null
                    ? []
                    : chain.Select((m, idx) => new AccessRequestItemStage { Order = idx + 1, MatrixId = m }).ToList(),
            });
        }

        if (request.Items.Count == 0)
            throw new InvalidOperationException(
                "Žádost je prázdná — na všechny vybrané čtečky/skupiny už běží nebo platí jiná žádost.");

        db.AccessRequests.Add(request);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(null, "request-created", "AccessRequest", request.Id.ToString(),
            $"zaměstnanec {targetEmployeeId}, položek {request.Items.Count}", ct);

        if (notifier is not null)
        {
            foreach (var item in request.Items)
            {
                if (item.Status == RequestStatus.Pending)
                    await notifier.NotifyPendingAsync(item.Id, ct);
                else if (item.Status == RequestStatus.Approved)
                    await notifier.NotifyDecidedAsync(item.Id, ct);
            }
        }

        return request;
    }

    // ---------- Kdo smí rozhodovat ----------

    /// <summary>Vrátí id uživatelů, za které smí <paramref name="userId"/> aktuálně jednat (on sám + zástupy).</summary>
    public async Task<HashSet<int>> GetActingIdentitiesAsync(int userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var principals = await db.Deputies
            .Where(d => d.DeputyUserId == userId && d.ValidFrom <= now && now <= d.ValidTo)
            .Select(d => d.PrincipalUserId)
            .ToListAsync(ct);
        return [userId, .. principals];
    }

    /// <summary>
    /// Položky čekající na rozhodnutí daného uživatele (včetně zástupů).
    /// Administrátor (<paramref name="isAdmin"/>) navíc vidí položky bez matice,
    /// které musí schválit ručně.
    /// </summary>
    public async Task<List<AccessRequestItem>> GetPendingForApproverAsync(
        int userId, bool isAdmin = false, CancellationToken ct = default)
    {
        var identities = await GetActingIdentitiesAsync(userId, ct);

        var items = await db.AccessRequestItems
            .Include(i => i.Request!).ThenInclude(r => r.TargetEmployee)
            .Include(i => i.Request!).ThenInclude(r => r.RequesterUser)
            .Include(i => i.Reader)
            .Include(i => i.ReaderGroup)
            .Include(i => i.Decisions)
            .Where(i => i.Status == RequestStatus.Pending)
            .ToListAsync(ct);

        var matrixIds = items.Where(i => i.MatrixId != null).Select(i => i.MatrixId!.Value).Distinct().ToList();
        var levels = await db.ApprovalLevels
            .Include(l => l.Approvers)
            .Where(l => matrixIds.Contains(l.MatrixId))
            .ToListAsync(ct);

        return items.Where(item =>
        {
            // Položka bez matice — smí rozhodnout jen administrátor.
            if (item.MatrixId is null)
                return isAdmin;

            var level = levels.FirstOrDefault(l =>
                l.MatrixId == item.MatrixId && l.Order == item.CurrentLevelOrder);
            if (level is null)
                return false;

            var approverIds = level.Approvers.Where(a => a.UserId != null).Select(a => a.UserId!.Value);
            var eligible = approverIds.Where(identities.Contains).ToList();
            if (eligible.Count == 0)
                return false;

            // Už na této úrovni (aktuální matice) rozhodl — sám nebo v zástupu?
            var decided = item.Decisions
                .Where(d => d.LevelOrder == item.CurrentLevelOrder
                            && (d.MatrixId ?? item.MatrixId) == item.MatrixId)
                .Select(d => d.OnBehalfOfUserId ?? d.ApproverUserId)
                .ToHashSet();
            return eligible.Any(id => !decided.Contains(id));
        }).ToList();
    }

    // ---------- Rozhodnutí ----------

    public async Task DecideAsync(int itemId, int userId, bool approve, string? comment,
        bool isAdmin = false, CancellationToken ct = default)
    {
        var item = await db.AccessRequestItems
            .Include(i => i.Decisions)
            .Include(i => i.Request)
            .FirstOrDefaultAsync(i => i.Id == itemId, ct)
            ?? throw new KeyNotFoundException("Položka žádosti nenalezena.");

        if (item.Status != RequestStatus.Pending)
            throw new InvalidOperationException("Položka nečeká na schválení.");

        // Položka bez matice — rozhoduje výhradně administrátor (žádné úrovně).
        if (item.MatrixId is null)
        {
            if (!isAdmin)
                throw new UnauthorizedAccessException(
                    "Položku bez schvalovací matice smí schválit pouze administrátor.");

            item.Decisions.Add(new ApprovalDecision
            {
                LevelOrder = 0,
                ApproverUserId = userId,
                Approved = approve,
                Comment = comment,
            });
            item.Status = approve ? RequestStatus.Approved : RequestStatus.Rejected;
            item.DecidedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
            await audit.LogAsync(null, approve ? "item-approved-admin" : "item-rejected-admin",
                "AccessRequestItem", item.Id.ToString(), $"administrátor {userId} (bez matice)", ct);
            if (notifier is not null)
                await notifier.NotifyDecidedAsync(item.Id, ct);
            return;
        }

        var level = await db.ApprovalLevels
            .Include(l => l.Approvers)
            .FirstOrDefaultAsync(l => l.MatrixId == item.MatrixId && l.Order == item.CurrentLevelOrder, ct)
            ?? throw new InvalidOperationException("Úroveň matice nenalezena.");

        var identities = await GetActingIdentitiesAsync(userId, ct);
        var levelApproverIds = level.Approvers
            .Where(a => a.UserId != null)
            .Select(a => a.UserId!.Value)
            .ToHashSet();

        // Za koho uživatel jedná: přednostně sám za sebe, jinak první zastoupený schvalovatel.
        int? onBehalfOf = null;
        if (!levelApproverIds.Contains(userId))
        {
            var principal = identities.FirstOrDefault(id => id != userId && levelApproverIds.Contains(id));
            if (principal == 0)
                throw new UnauthorizedAccessException("Nejste schvalovatelem této úrovně (ani zástupem).");
            onBehalfOf = principal;
        }

        var effectiveIdentity = onBehalfOf ?? userId;
        var alreadyDecided = item.Decisions
            .Where(d => d.LevelOrder == item.CurrentLevelOrder
                        && (d.MatrixId ?? item.MatrixId) == item.MatrixId)
            .Any(d => (d.OnBehalfOfUserId ?? d.ApproverUserId) == effectiveIdentity);
        if (alreadyDecided)
            throw new InvalidOperationException("Na této úrovni už bylo za tohoto schvalovatele rozhodnuto.");

        item.Decisions.Add(new ApprovalDecision
        {
            LevelOrder = item.CurrentLevelOrder,
            MatrixId = item.MatrixId,
            ApproverUserId = userId,
            OnBehalfOfUserId = onBehalfOf,
            Approved = approve,
            Comment = comment,
        });

        if (!approve)
        {
            item.Status = RequestStatus.Rejected;
            item.DecidedAt = DateTime.UtcNow;
        }
        else if (IsLevelSatisfied(level, item))
        {
            var nextOrder = await db.ApprovalLevels
                .Where(l => l.MatrixId == item.MatrixId && l.Order > item.CurrentLevelOrder)
                .OrderBy(l => l.Order)
                .Select(l => (int?)l.Order)
                .FirstOrDefaultAsync(ct);

            if (nextOrder is not null)
            {
                item.CurrentLevelOrder = nextOrder.Value;
            }
            else
            {
                // Aktuální matice dokončena — existuje další fáze řetězu
                // (vnořené schvalování, např. skupina → bezpečnost)?
                var nextStage = await db.AccessRequestItemStages
                    .Where(s => s.ItemId == item.Id && s.Order > item.CurrentStageOrder)
                    .OrderBy(s => s.Order)
                    .FirstOrDefaultAsync(ct);
                var nextFirstLevel = nextStage is null
                    ? null
                    : await db.ApprovalLevels
                        .Where(l => l.MatrixId == nextStage.MatrixId)
                        .MinAsync(l => (int?)l.Order, ct);

                if (nextStage is not null && nextFirstLevel is not null)
                {
                    item.CurrentStageOrder = nextStage.Order;
                    item.MatrixId = nextStage.MatrixId;
                    item.CurrentLevelOrder = nextFirstLevel.Value;
                }
                else
                {
                    item.Status = RequestStatus.Approved;
                    item.DecidedAt = DateTime.UtcNow;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        await audit.LogAsync(null, approve ? "item-approved" : "item-rejected",
            "AccessRequestItem", item.Id.ToString(),
            onBehalfOf is null ? $"uživatel {userId}" : $"uživatel {userId} v zástupu za {onBehalfOf}", ct);

        if (notifier is not null)
        {
            if (item.Status is RequestStatus.Approved or RequestStatus.Rejected)
                await notifier.NotifyDecidedAsync(item.Id, ct);
            else if (item.Status == RequestStatus.Pending)
                await notifier.NotifyPendingAsync(item.Id, ct); // postup na další úroveň
        }
    }

    /// <summary>Vyhodnotí, zda je úroveň po posledním schválení splněna (Any / All / Quorum).</summary>
    private static bool IsLevelSatisfied(ApprovalLevel level, AccessRequestItem item)
    {
        var approvals = item.Decisions
            .Where(d => d.LevelOrder == level.Order && d.Approved
                        && (d.MatrixId ?? level.MatrixId) == level.MatrixId)
            .Select(d => d.OnBehalfOfUserId ?? d.ApproverUserId)
            .Distinct()
            .Count();

        var totalApprovers = level.Approvers.Count(a => a.UserId != null);
        return level.Mode switch
        {
            ApprovalMode.Any => approvals >= 1,
            ApprovalMode.All => approvals >= Math.Max(1, totalApprovers),
            ApprovalMode.Quorum => approvals >= Math.Max(1, level.RequiredCount ?? 1),
            _ => false,
        };
    }
}
