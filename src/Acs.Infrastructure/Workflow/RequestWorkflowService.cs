using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Workflow;

/// <summary>Vstup žádosti o parkovací povolení.</summary>
/// <param name="PermitTypeId">Druh povolení.</param>
/// <param name="AllSites">Platí pro všechny areály (pak se <paramref name="SiteIds"/> ignoruje).</param>
/// <param name="SiteIds">Vybrané areály.</param>
/// <param name="Plates">SPZ (u vazby na SPZ); normalizují se.</param>
/// <param name="FunctionTitle">Funkce (u vazby na funkci).</param>
/// <param name="ValidFrom">Začátek platnosti (null = dnes).</param>
/// <param name="ValidTo">Konec platnosti (null = podle výchozí délky druhu, jinak bez omezení).</param>
public record ParkingRequestInput(
    int PermitTypeId,
    bool AllSites,
    IReadOnlyCollection<int>? SiteIds,
    IReadOnlyCollection<string>? Plates,
    string? FunctionTitle,
    DateTime? ValidFrom,
    DateTime? ValidTo,
    string? Justification);

/// <summary>Vstup žádosti o kamery / EZS.</summary>
public record SecurityRequestInput(
    SecurityRequestKind Kind,
    string Title,
    string? Description,
    int? BuildingId,
    int? FloorId,
    int? RoomId,
    string? LocationText,
    string? Justification);

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
///   <item>čtečka bez aktivní matice se <b>neschvaluje automaticky</b> — použije se
///     výchozí matice (<see cref="ApprovalMatrix.IsDefault"/>, typicky nadřízený), a když
///     není, vyžaduje rozhodnutí administrátora (žádný přístup neobejde lidské schválení),</item>
///   <item>úrovně matice mohou být <b>podmíněné</b> kategorií zaměstnance a vztahem k prostoru
///     (vlastní / cizí úsek, <see cref="ApprovalContext"/>) — neplatné úrovně se přeskočí; když
///     neplatí žádná, položka se schválí bez stupně jen u matice s
///     <see cref="ApprovalMatrix.AutoApproveWhenNoLevels"/>, jinak ji rozhoduje administrátor.</item>
/// </list>
/// </summary>
public class RequestWorkflowService(
    AcsDbContext db, AuditService audit,
    INotificationService? notifier = null, ReaderGroupService? groups = null)
{
    private ReaderGroupService Groups => groups ?? new ReaderGroupService(db);

    // ---------- Podání žádosti ----------

    /// <summary>
    /// Výchozí matice (<see cref="ApprovalMatrix.IsDefault"/>) — použije se pro čtečky,
    /// skupiny a parkovací povolení bez vlastní matice, aby žádost nešla rovnou
    /// k administrátorovi, ale např. k nadřízenému zaměstnance. Null, když není
    /// nastavena, není aktivní nebo nemá úrovně.
    /// </summary>
    public async Task<ApprovalMatrix?> GetDefaultMatrixAsync(CancellationToken ct = default)
        => await db.ApprovalMatrices
            .Include(m => m.Levels)
            .Where(m => m.IsDefault && m.IsActive && m.Levels.Any())
            .OrderBy(m => m.Id)
            .FirstOrDefaultAsync(ct);

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

        await EnsureCanRequestForAsync(requesterUserId, targetEmployeeId, requesterCanActForOthers, ct);

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

        var defaultMatrix = await GetDefaultMatrixAsync(ct);
        var contexts = new ApprovalContextResolver(db);
        var matrixCache = new Dictionary<int, ApprovalMatrix>();
        foreach (var reader in readers.Where(r => !skip.Contains(r.Id)))
        {
            var matrix = reader.ApprovalMatrix is { IsActive: true, Levels.Count: > 0 }
                ? reader.ApprovalMatrix
                : defaultMatrix;

            // Čtečka bez matice (a bez výchozí matice) se NESCHVALUJE automaticky —
            // zůstává Pending a smí ji schválit pouze administrátor (viz GetPendingForApproverAsync).
            var item = new AccessRequestItem
            {
                ReaderId = reader.Id,
                AutoAdded = !explicitIds.Contains(reader.Id),
                Status = RequestStatus.Pending,
            };
            var context = await contexts.ForReaderAsync(targetEmployeeId, reader.Id, ct);
            await StartItemAsync(item, matrix is null ? [] : [matrix.Id], context, matrixCache, ct, trackStages: false);
            request.Items.Add(item);
        }

        // Skupinové položky: řetěz matic = matice skupiny + matic nadřazených skupin.
        foreach (var groupId in groupIds.Where(g => !skipGroups.Contains(g)))
        {
            var chain = await Groups.GetMatrixChainAsync(groupId, ct);
            if (chain.Count == 0 && defaultMatrix is not null)
                chain = [defaultMatrix.Id];

            var item = new AccessRequestItem
            {
                ReaderGroupId = groupId,
                Status = RequestStatus.Pending,
            };
            var context = await contexts.ForGroupAsync(targetEmployeeId, groupId, ct);
            await StartItemAsync(item, chain, context, matrixCache, ct);
            request.Items.Add(item);
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
                    await NotifyPendingOrFallbackAsync(item, ct);
                else if (item.Status == RequestStatus.Approved)
                    await notifier.NotifyDecidedAsync(item.Id, ct);
            }
        }

        return request;
    }

    /// <summary>Autorizace: kdo smí žádat za koho.</summary>
    private async Task EnsureCanRequestForAsync(
        int requesterUserId, int targetEmployeeId, bool requesterCanActForOthers, CancellationToken ct)
    {
        if (requesterCanActForOthers)
            return;

        var ownEmployeeId = await db.Users
            .Where(u => u.Id == requesterUserId)
            .Select(u => u.EmployeeId)
            .FirstOrDefaultAsync(ct);
        if (ownEmployeeId is null || ownEmployeeId.Value != targetEmployeeId)
            throw new UnauthorizedAccessException(
                "Nemáte oprávnění podat žádost za jiného zaměstnance.");
    }

    // ---------- Parkovací povolení ----------

    /// <summary>Stavy, ve kterých parkovací povolení „běží nebo platí“ (blokují duplicitní žádost).</summary>
    public static readonly RequestStatus[] ActiveParkingStatuses =
        [RequestStatus.Pending, RequestStatus.Approved, RequestStatus.Issued];

    /// <summary>
    /// Podání žádosti o parkovací povolení. Povolení je vázané buď na SPZ (jednu či více,
    /// podle <see cref="ParkingPermitType.MaxPlates"/>), nebo na funkci; platí pro vybrané
    /// nebo všechny areály. Řetěz schvalování = matice druhu povolení a poté matice
    /// zvolených areálů (u „všech areálů“ všech aktivních areálů). Bez jakékoli matice
    /// zůstává položka čekat na rozhodnutí administrátora — nic se neschvaluje automaticky.
    /// </summary>
    public async Task<AccessRequest> CreateParkingRequestAsync(
        int requesterUserId, int targetEmployeeId, ParkingRequestInput input,
        bool requesterCanActForOthers = false, CancellationToken ct = default)
    {
        await EnsureCanRequestForAsync(requesterUserId, targetEmployeeId, requesterCanActForOthers, ct);

        var type = await db.ParkingPermitTypes
            .Include(t => t.ApprovalMatrix!).ThenInclude(m => m.Levels)
            .FirstOrDefaultAsync(t => t.Id == input.PermitTypeId, ct)
            ?? throw new InvalidOperationException("Druh parkovacího povolení nenalezen.");
        if (!type.IsActive)
            throw new InvalidOperationException($"Druh povolení „{type.Name}“ není aktivní.");

        if (!await db.Employees.AnyAsync(e => e.Id == targetEmployeeId, ct))
            throw new InvalidOperationException("Zaměstnanec nenalezen.");

        // Vazba: SPZ, nebo funkce.
        List<string> plates = [];
        string? functionTitle = null;
        if (type.Binding == PermitBinding.LicensePlate)
        {
            plates = (input.Plates ?? [])
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(EmployeeIdentifier.Normalize)
                .Distinct()
                .ToList();
            if (plates.Count == 0)
                throw new InvalidOperationException("Zadejte alespoň jednu registrační značku (SPZ).");
            if (plates.Count > Math.Max(1, type.MaxPlates))
                throw new InvalidOperationException(
                    $"Druh povolení „{type.Name}“ dovoluje nejvýše {Math.Max(1, type.MaxPlates)} SPZ.");
            var invalid = plates.FirstOrDefault(p => p.Length is < 2 or > 12 || !p.All(char.IsLetterOrDigit));
            if (invalid is not null)
                throw new InvalidOperationException($"„{invalid}“ nevypadá jako platná registrační značka.");
        }
        else
        {
            functionTitle = input.FunctionTitle?.Trim();
            if (string.IsNullOrWhiteSpace(functionTitle))
                throw new InvalidOperationException("U povolení na funkci zadejte název funkce.");
        }

        // Areály.
        List<Site> sites;
        if (input.AllSites)
        {
            sites = await db.Sites.Where(s => s.IsActive).OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync(ct);
        }
        else
        {
            var siteIds = (input.SiteIds ?? []).Distinct().ToList();
            if (siteIds.Count == 0)
                throw new InvalidOperationException("Vyberte alespoň jeden areál, nebo zvolte „všechny areály“.");
            sites = await db.Sites.Where(s => siteIds.Contains(s.Id) && s.IsActive)
                .OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync(ct);
            if (sites.Count != siteIds.Count)
                throw new InvalidOperationException("Některý z vybraných areálů neexistuje nebo není aktivní.");
        }

        // Platnost.
        var validFrom = (input.ValidFrom ?? DateTime.UtcNow.Date);
        var validTo = input.ValidTo
            ?? (type.DefaultValidityMonths is { } months ? validFrom.AddMonths(months) : null);
        if (validTo is not null && validTo <= validFrom)
            throw new InvalidOperationException("Konec platnosti musí být později než začátek.");

        // Duplicita: na stejný druh už zaměstnanec má běžící nebo platné povolení.
        var duplicate = await db.AccessRequestItems.AnyAsync(i =>
            i.ParkingPermitId != null
            && i.ParkingPermit!.PermitTypeId == type.Id
            && i.Request!.TargetEmployeeId == targetEmployeeId
            && i.Request.Kind == RequestKind.Grant
            && ActiveParkingStatuses.Contains(i.Status), ct);
        if (duplicate)
            throw new InvalidOperationException(
                $"Na povolení „{type.Name}“ už tomuto zaměstnanci běží nebo platí jiná žádost.");

        // Řetěz matic: druh → areály (bez duplicit, jen aktivní matice s úrovněmi).
        var chain = new List<int>();
        if (type.ApprovalMatrix is { IsActive: true, Levels.Count: > 0 })
            chain.Add(type.ApprovalMatrix.Id);
        var siteMatrixIds = sites.Where(s => s.ApprovalMatrixId != null).Select(s => s.ApprovalMatrixId!.Value).Distinct().ToList();
        if (siteMatrixIds.Count > 0)
        {
            var usable = await db.ApprovalMatrices
                .Where(m => siteMatrixIds.Contains(m.Id) && m.IsActive && m.Levels.Any())
                .Select(m => m.Id)
                .ToListAsync(ct);
            foreach (var site in sites)
            {
                if (site.ApprovalMatrixId is { } mid && usable.Contains(mid) && !chain.Contains(mid))
                    chain.Add(mid);
            }
        }

        // Bez jakékoli matice (druh i areály) → výchozí matice, je-li nastavena.
        if (chain.Count == 0 && await GetDefaultMatrixAsync(ct) is { } defaultMatrix)
            chain.Add(defaultMatrix.Id);

        var permit = new ParkingPermit
        {
            EmployeeId = targetEmployeeId,
            PermitTypeId = type.Id,
            FunctionTitle = functionTitle,
            AllSites = input.AllSites,
            Sites = input.AllSites ? [] : sites.Select(s => new ParkingPermitSite { SiteId = s.Id }).ToList(),
            Plates = plates.Select(p => new ParkingPermitPlate { Value = p }).ToList(),
            ValidFrom = validFrom,
            ValidTo = validTo,
        };

        var item = new AccessRequestItem { ParkingPermit = permit, Status = RequestStatus.Pending };
        var context = await new ApprovalContextResolver(db).ForEmployeeAsync(targetEmployeeId, ct);
        await StartItemAsync(item, chain, context, new Dictionary<int, ApprovalMatrix>(), ct);

        var request = new AccessRequest
        {
            Kind = RequestKind.Grant,
            RequesterUserId = requesterUserId,
            TargetEmployeeId = targetEmployeeId,
            Justification = input.Justification,
            ValidUntil = validTo,
            Items = [item],
        };

        db.AccessRequests.Add(request);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(null, "parking-request-created", "ParkingPermit", permit.Id.ToString(),
            $"zaměstnanec {targetEmployeeId}, druh {type.Name}, {permit.SubjectText()}, {(input.AllSites ? "všechny areály" : string.Join(", ", sites.Select(s => s.Name)))}", ct);

        if (notifier is not null)
        {
            if (item.Status == RequestStatus.Approved)
                await notifier.NotifyDecidedAsync(item.Id, ct);
            else
                await NotifyPendingOrFallbackAsync(item, ct);
        }

        return request;
    }

    // ---------- Kamery / EZS ----------

    /// <summary>
    /// Podání žádosti o zřízení / rozšíření kamerového systému nebo EZS. Cílový zaměstnanec je
    /// žadatel (vedoucí úseku); <paramref name="matrixId"/> je matice nastavená pro kamery / EZS
    /// (Katalog → Schvalovací matice), bez ní výchozí matice, bez obojího rozhoduje administrátor.
    /// Schválená položka jde do fronty realizace ICT (<see cref="AppRole.IctAdmin"/>).
    /// </summary>
    public async Task<AccessRequest> CreateSecurityRequestAsync(
        int requesterUserId, int targetEmployeeId, SecurityRequestInput input, int? matrixId,
        bool requesterCanActForOthers = false, CancellationToken ct = default)
    {
        await EnsureCanRequestForAsync(requesterUserId, targetEmployeeId, requesterCanActForOthers, ct);

        var title = input.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("Zadejte název požadavku.");
        if (string.IsNullOrWhiteSpace(input.Description) && string.IsNullOrWhiteSpace(input.Justification))
            throw new InvalidOperationException("Popište, co a proč se má zřídit nebo rozšířit.");

        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == targetEmployeeId, ct)
            ?? throw new InvalidOperationException("Zaměstnanec nenalezen.");

        if (input.RoomId is { } roomId && !await db.Rooms.AnyAsync(r => r.Id == roomId, ct))
            throw new InvalidOperationException("Vybraná místnost neexistuje.");
        if (input.FloorId is { } floorId && !await db.Floors.AnyAsync(f => f.Id == floorId, ct))
            throw new InvalidOperationException("Vybrané patro neexistuje.");
        if (input.BuildingId is { } buildingId && !await db.Buildings.AnyAsync(b => b.Id == buildingId, ct))
            throw new InvalidOperationException("Vybraná budova neexistuje.");

        var chain = new List<int>();
        if (matrixId is { } mid
            && await db.ApprovalMatrices.AnyAsync(m => m.Id == mid && m.IsActive && m.Levels.Any(), ct))
            chain.Add(mid);
        if (chain.Count == 0 && await GetDefaultMatrixAsync(ct) is { } defaultMatrix)
            chain.Add(defaultMatrix.Id);

        var security = new SecurityRequest
        {
            Kind = input.Kind,
            Title = title,
            Description = input.Description?.Trim(),
            BuildingId = input.BuildingId,
            FloorId = input.FloorId,
            RoomId = input.RoomId,
            LocationText = input.LocationText?.Trim(),
            OrgUnitId = employee.OrgUnitId,
        };

        var item = new AccessRequestItem { SecurityRequest = security, Status = RequestStatus.Pending };
        var context = await new ApprovalContextResolver(db).ForEmployeeAsync(targetEmployeeId, ct);
        await StartItemAsync(item, chain, context, new Dictionary<int, ApprovalMatrix>(), ct);

        var request = new AccessRequest
        {
            Kind = RequestKind.Grant,
            RequesterUserId = requesterUserId,
            TargetEmployeeId = targetEmployeeId,
            Justification = input.Justification,
            Items = [item],
        };

        db.AccessRequests.Add(request);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(null, "security-request-created", "SecurityRequest", security.Id.ToString(),
            $"zaměstnanec {targetEmployeeId}, {security.KindLabel}: {title}", ct);

        if (notifier is not null)
        {
            if (item.Status == RequestStatus.Approved)
                await notifier.NotifyDecidedAsync(item.Id, ct);
            else
                await NotifyPendingOrFallbackAsync(item, ct);
        }

        return request;
    }

    // ---------- Průchod úrovněmi ----------

    private sealed record LevelPosition(int StageOrder, int MatrixId, int LevelOrder);

    private async Task<Dictionary<int, ApprovalMatrix>> LoadMatricesAsync(
        IEnumerable<int> ids, Dictionary<int, ApprovalMatrix> cache, CancellationToken ct)
    {
        var missing = ids.Distinct().Where(id => !cache.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var loaded = await db.ApprovalMatrices.AsNoTracking()
                .Include(m => m.Levels)
                .Where(m => missing.Contains(m.Id))
                .ToListAsync(ct);
            foreach (var matrix in loaded)
                cache[matrix.Id] = matrix;
        }

        return cache;
    }

    /// <summary>
    /// Další platná úroveň v řetězu matic za pozicí (<paramref name="fromStage"/>, <paramref name="fromLevelOrder"/>);
    /// úrovně, které pro kontext položky neplatí, se přeskočí. Null = řetěz vyčerpán.
    /// </summary>
    private static LevelPosition? FindNextLevel(
        IReadOnlyList<int> chain, int fromStage, int fromLevelOrder, ApprovalContext context,
        IReadOnlyDictionary<int, ApprovalMatrix> matrices)
    {
        for (var stage = Math.Max(1, fromStage); stage <= chain.Count; stage++)
        {
            if (!matrices.TryGetValue(chain[stage - 1], out var matrix))
                continue;
            var minOrder = stage == fromStage ? fromLevelOrder : 0;
            var level = matrix.Levels
                .Where(l => l.Order > minOrder)
                .OrderBy(l => l.Order)
                .FirstOrDefault(l => context.Applies(l, matrix.TreatUnknownUnitAsOutside));
            if (level is not null)
                return new LevelPosition(stage, matrix.Id, level.Order);
        }

        return null;
    }

    /// <summary>
    /// Nastaví novou položku na první platnou úroveň řetězu matic. Bez řetězu (nebo s maticí bez
    /// úrovní) rozhoduje administrátor. Když žádná úroveň neplatí (podmínky kategorie / úseku):
    /// všechny matice řetězu s <see cref="ApprovalMatrix.AutoApproveWhenNoLevels"/> → schváleno
    /// bez stupně; jinak administrátor.
    /// </summary>
    private async Task StartItemAsync(
        AccessRequestItem item, IReadOnlyList<int> chain, ApprovalContext context,
        Dictionary<int, ApprovalMatrix> matrixCache, CancellationToken ct, bool trackStages = true)
    {
        item.CurrentStageOrder = 1;
        if (chain.Count == 0)
        {
            item.MatrixId = null;
            item.CurrentLevelOrder = 0;
            return;
        }

        var matrices = await LoadMatricesAsync(chain, matrixCache, ct);
        var usable = chain.Where(id => matrices.TryGetValue(id, out var m) && m.Levels.Count > 0).ToList();
        if (usable.Count == 0)
        {
            item.MatrixId = null; // matice bez úrovní → jako bez matice (schvaluje admin)
            item.CurrentLevelOrder = 0;
            return;
        }

        if (trackStages || usable.Count > 1)
            item.Stages = usable.Select((m, idx) => new AccessRequestItemStage { Order = idx + 1, MatrixId = m }).ToList();

        var next = FindNextLevel(usable, 1, 0, context, matrices);
        if (next is not null)
        {
            item.MatrixId = next.MatrixId;
            item.CurrentStageOrder = next.StageOrder;
            item.CurrentLevelOrder = next.LevelOrder;
            return;
        }

        if (usable.All(id => matrices[id].AutoApproveWhenNoLevels))
        {
            // Bez schvalovacího stupně (např. ředitel, náměstek ve vlastním úseku) → rovnou realizace.
            item.MatrixId = usable[0];
            item.CurrentLevelOrder = 0;
            item.Status = RequestStatus.Approved;
            item.DecidedAt = DateTime.UtcNow;
            item.PushResult = null;
            item.Stages = [];
            item.AutoApproved = true;
            return;
        }

        item.MatrixId = null;
        item.CurrentLevelOrder = 0;
        item.Stages = [];
    }

    /// <summary>Řetěz matic položky (fáze, nebo jen aktuální matice).</summary>
    private static List<int> ChainOf(AccessRequestItem item)
        => item.Stages.Count > 0
            ? item.Stages.OrderBy(s => s.Order).Select(s => s.MatrixId).ToList()
            : item.MatrixId is { } mid ? [mid] : [];

    /// <summary>
    /// Žádost o odebrání vydaného parkovacího povolení. Neprochází schvalováním —
    /// jde rovnou do fronty správce parkování (jako vrácení karty).
    /// </summary>
    public async Task<AccessRequest> CreateParkingRevokeRequestAsync(
        int requesterUserId, int permitId, string? reason,
        bool requesterCanActForOthers = false, CancellationToken ct = default)
    {
        var permit = await db.ParkingPermits.Include(p => p.PermitType)
            .FirstOrDefaultAsync(p => p.Id == permitId, ct)
            ?? throw new KeyNotFoundException("Parkovací povolení nenalezeno.");

        await EnsureCanRequestForAsync(requesterUserId, permit.EmployeeId, requesterCanActForOthers, ct);

        var issued = await db.AccessRequestItems.AnyAsync(i =>
            i.ParkingPermitId == permitId && i.Request!.Kind == RequestKind.Grant
            && i.Status == RequestStatus.Issued, ct);
        if (!issued)
            throw new InvalidOperationException("Povolení není vydané — není co odebírat.");

        var alreadyRequested = await db.AccessRequestItems.AnyAsync(i =>
            i.ParkingPermitId == permitId && i.Request!.Kind == RequestKind.Revoke
            && (i.Status == RequestStatus.Pending || i.Status == RequestStatus.Approved), ct);
        if (alreadyRequested)
            throw new InvalidOperationException("O odebrání tohoto povolení už je požádáno.");

        var request = new AccessRequest
        {
            Kind = RequestKind.Revoke,
            RequesterUserId = requesterUserId,
            TargetEmployeeId = permit.EmployeeId,
            Justification = reason ?? "žádost o odebrání parkovacího povolení",
            Items =
            [
                new AccessRequestItem
                {
                    ParkingPermitId = permitId,
                    Status = RequestStatus.Approved,
                    CurrentLevelOrder = 0,
                    DecidedAt = DateTime.UtcNow,
                },
            ],
        };
        db.AccessRequests.Add(request);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(null, "parking-revoke-requested", "ParkingPermit", permitId.ToString(),
            $"uživatel {requesterUserId}", ct);

        if (notifier is not null)
            await notifier.NotifyDecidedAsync(request.Items[0].Id, ct);

        return request;
    }

    // ---------- Kdo smí rozhodovat ----------

    /// <summary>Vrátí id uživatelů, za které smí <paramref name="userId"/> aktuálně jednat (on sám + zástupy).</summary>
    public async Task<HashSet<int>> GetActingIdentitiesAsync(int userId, CancellationToken ct = default)
        => (await new ApproverResolver(db).GetActingIdentityAsync(userId, ct)).UserIds;

    /// <summary>
    /// Vyhodnocení aktuální úrovně položky — kdo na ní smí rozhodnout (konkrétní
    /// uživatelé + nadřízený cílového zaměstnance). Null u položky bez matice nebo
    /// bez odpovídající úrovně. Pro zobrazení v detailu/seznamu žádostí.
    /// </summary>
    public async Task<LevelResolution?> ResolveCurrentLevelAsync(AccessRequestItem item, CancellationToken ct = default)
    {
        if (item.MatrixId is null)
            return null;
        var level = await db.ApprovalLevels.AsNoTracking()
            .Include(l => l.Approvers)
            .FirstOrDefaultAsync(l => l.MatrixId == item.MatrixId && l.Order == item.CurrentLevelOrder, ct);
        return level is null ? null : await new ApproverResolver(db).ResolveAsync(level, item, ct);
    }

    /// <summary>Kontext položek (kategorie zaměstnance, úsek prostoru, vlastní / cizí) — pro zobrazení v detailu.</summary>
    public async Task<Dictionary<int, ApprovalContext>> ResolveContextsAsync(
        IReadOnlyCollection<AccessRequestItem> items, CancellationToken ct = default)
    {
        var resolver = new ApprovalContextResolver(db);
        var result = new Dictionary<int, ApprovalContext>();
        foreach (var item in items)
            result[item.Id] = await resolver.ForItemAsync(item, ct);
        return result;
    }

    /// <summary>Vyhodnocení aktuálních úrovní pro víc položek najednou (sdílená cache zaměstnanců a uživatelů).</summary>
    public async Task<Dictionary<int, LevelResolution>> ResolveCurrentLevelsAsync(
        IReadOnlyCollection<AccessRequestItem> items, CancellationToken ct = default)
    {
        var result = new Dictionary<int, LevelResolution>();
        var matrixIds = items.Where(i => i.MatrixId != null).Select(i => i.MatrixId!.Value).Distinct().ToList();
        if (matrixIds.Count == 0)
            return result;

        var levels = await db.ApprovalLevels.AsNoTracking()
            .Include(l => l.Approvers)
            .Where(l => matrixIds.Contains(l.MatrixId))
            .ToListAsync(ct);
        var resolver = new ApproverResolver(db);
        foreach (var item in items)
        {
            if (item.MatrixId is null)
                continue;
            var level = levels.FirstOrDefault(l => l.MatrixId == item.MatrixId && l.Order == item.CurrentLevelOrder);
            if (level is not null)
                result[item.Id] = await resolver.ResolveAsync(level, item, ct);
        }

        return result;
    }

    /// <summary>
    /// Položky čekající na rozhodnutí daného uživatele (včetně zástupů).
    /// Administrátor (<paramref name="isAdmin"/>) navíc vidí položky bez matice,
    /// které musí schválit ručně.
    /// </summary>
    public async Task<List<AccessRequestItem>> GetPendingForApproverAsync(
        int userId, bool isAdmin = false, CancellationToken ct = default)
    {
        var resolver = new ApproverResolver(db);
        var identity = await resolver.GetActingIdentityAsync(userId, ct);

        var items = await db.AccessRequestItems
            .Include(i => i.Request!).ThenInclude(r => r.TargetEmployee)
            .Include(i => i.Request!).ThenInclude(r => r.RequesterUser)
            .Include(i => i.Reader)
            .Include(i => i.ReaderGroup)
            .Include(i => i.ParkingPermit!).ThenInclude(p => p.PermitType)
            .Include(i => i.ParkingPermit!).ThenInclude(p => p.Plates)
            .Include(i => i.ParkingPermit!).ThenInclude(p => p.Sites).ThenInclude(s => s.Site)
            .Include(i => i.Decisions)
            .Where(i => i.Status == RequestStatus.Pending)
            .ToListAsync(ct);

        var matrixIds = items.Where(i => i.MatrixId != null).Select(i => i.MatrixId!.Value).Distinct().ToList();
        var levels = await db.ApprovalLevels
            .Include(l => l.Approvers)
            .Where(l => matrixIds.Contains(l.MatrixId))
            .ToListAsync(ct);

        var result = new List<AccessRequestItem>();
        foreach (var item in items)
        {
            // Položka bez matice — smí rozhodnout jen administrátor.
            if (item.MatrixId is null)
            {
                if (isAdmin)
                    result.Add(item);
                continue;
            }

            var level = levels.FirstOrDefault(l =>
                l.MatrixId == item.MatrixId && l.Order == item.CurrentLevelOrder);
            if (level is null)
                continue;

            var resolution = await resolver.ResolveAsync(level, item, ct);

            // Úroveň bez schvalovatele (zaměstnanec bez nadřízeného) — rozhoduje administrátor.
            if (resolution.RequiresAdminFallback)
            {
                if (isAdmin)
                    result.Add(item);
                continue;
            }

            var eligible = resolution.Approvers.Where(a => a.Matches(identity)).ToList();
            if (eligible.Count == 0)
                continue;

            // Už na této úrovni (aktuální matice) rozhodl — sám nebo v zástupu?
            var decided = item.Decisions
                .Where(d => d.LevelOrder == item.CurrentLevelOrder
                            && (d.MatrixId ?? item.MatrixId) == item.MatrixId)
                .Select(d => d.OnBehalfOfUserId ?? d.ApproverUserId)
                .ToHashSet();
            if (eligible.Any(a => a.UserId is null || !decided.Contains(a.UserId.Value)))
                result.Add(item);
        }

        return result;
    }

    // ---------- Rozhodnutí ----------

    public async Task DecideAsync(int itemId, int userId, bool approve, string? comment,
        bool isAdmin = false, CancellationToken ct = default)
    {
        var item = await db.AccessRequestItems
            .Include(i => i.Decisions)
            .Include(i => i.Request)
            .Include(i => i.Stages)
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

        var resolver = new ApproverResolver(db);
        var identity = await resolver.GetActingIdentityAsync(userId, ct);
        var resolution = await resolver.ResolveAsync(level, item, ct);

        // Úroveň bez schvalovatele (např. zaměstnanec bez nadřízeného) — rozhoduje
        // administrátor místo chybějícího schvalovatele; důvod se zapíše k rozhodnutí.
        if (resolution.RequiresAdminFallback)
        {
            if (!isAdmin)
                throw new UnauthorizedAccessException(
                    "Na této úrovni není žádný schvalovatel (" + string.Join("; ", resolution.Warnings)
                    + ") — rozhodnout smí pouze administrátor.");
            var reason = resolution.Warnings.Count > 0 ? string.Join("; ", resolution.Warnings) : "úroveň bez schvalovatele";
            comment = string.IsNullOrWhiteSpace(comment)
                ? $"[rozhodl administrátor — {reason}]"
                : $"[rozhodl administrátor — {reason}] {comment}";
        }

        // Za koho uživatel jedná: přednostně sám za sebe, jinak první zastoupený schvalovatel.
        int? onBehalfOf = null;
        if (!resolution.RequiresAdminFallback)
        {
            var self = identity.SelfOnly();
            if (!resolution.Approvers.Any(a => a.Matches(self)))
            {
                var principal = resolution.Approvers
                    .FirstOrDefault(a => a.UserId is { } uid && uid != userId && identity.UserIds.Contains(uid));
                if (principal is null)
                    throw new UnauthorizedAccessException("Nejste schvalovatelem této úrovně (ani zástupem).");
                onBehalfOf = principal.UserId;
            }
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
        else if (resolution.RequiresAdminFallback || IsLevelSatisfied(level, item, resolution.Approvers.Count))
        {
            // Další platná úroveň — v aktuální matici, nebo v další fázi řetězu (vnořené
            // schvalování, např. skupina → bezpečnost). Úrovně neplatné pro kategorii /
            // úsek se přeskočí; vyčerpaný řetěz = schváleno.
            var chain = ChainOf(item);
            var matrices = await LoadMatricesAsync(chain, new Dictionary<int, ApprovalMatrix>(), ct);
            var context = await resolver.Contexts.ForItemAsync(item, ct);
            var next = FindNextLevel(chain, item.CurrentStageOrder, item.CurrentLevelOrder, context, matrices);
            if (next is not null)
            {
                item.CurrentStageOrder = next.StageOrder;
                item.MatrixId = next.MatrixId;
                item.CurrentLevelOrder = next.LevelOrder;
            }
            else
            {
                item.Status = RequestStatus.Approved;
                item.DecidedAt = DateTime.UtcNow;
            }
        }

        await db.SaveChangesAsync(ct);
        await audit.LogAsync(null, approve ? "item-approved" : "item-rejected",
            "AccessRequestItem", item.Id.ToString(),
            resolution.RequiresAdminFallback
                ? $"administrátor {userId} místo chybějícího schvalovatele"
                : onBehalfOf is null ? $"uživatel {userId}" : $"uživatel {userId} v zástupu za {onBehalfOf}", ct);

        if (notifier is not null)
        {
            if (item.Status is RequestStatus.Approved or RequestStatus.Rejected)
                await notifier.NotifyDecidedAsync(item.Id, ct);
            else if (item.Status == RequestStatus.Pending)
                await NotifyPendingOrFallbackAsync(item, ct); // postup na další úroveň
        }
    }

    /// <summary>
    /// Upozorní schvalovatele aktuální úrovně; když úroveň po vyhodnocení nikoho nemá
    /// (zaměstnanec bez nadřízeného), upozorní místo toho administrátory, že mají rozhodnout.
    /// </summary>
    private async Task NotifyPendingOrFallbackAsync(AccessRequestItem item, CancellationToken ct)
    {
        if (notifier is null)
            return;

        var resolution = item.MatrixId is null ? null : await ResolveCurrentLevelAsync(item, ct);
        if (resolution is { RequiresAdminFallback: true })
        {
            await notifier.NotifyNoApproverAsync(item.Id,
                resolution.Warnings.Count > 0 ? string.Join("; ", resolution.Warnings) : "úroveň bez schvalovatele", ct);
            return;
        }

        await notifier.NotifyPendingAsync(item.Id, ct);
    }

    /// <summary>
    /// Vyhodnotí, zda je úroveň po posledním schválení splněna (Any / All / Quorum).
    /// <paramref name="totalApprovers"/> = konkrétní uživatelé + vyhodnocení nadřízení.
    /// </summary>
    private static bool IsLevelSatisfied(ApprovalLevel level, AccessRequestItem item, int totalApprovers)
    {
        var approvals = item.Decisions
            .Where(d => d.LevelOrder == level.Order && d.Approved
                        && (d.MatrixId ?? level.MatrixId) == level.MatrixId)
            .Select(d => d.OnBehalfOfUserId ?? d.ApproverUserId)
            .Distinct()
            .Count();

        return level.Mode switch
        {
            ApprovalMode.Any => approvals >= 1,
            ApprovalMode.All => approvals >= Math.Max(1, totalApprovers),
            ApprovalMode.Quorum => approvals >= Math.Max(1, level.RequiredCount ?? 1),
            _ => false,
        };
    }
}
