using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Organization;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Workflow;

/// <summary>
/// Vlastnictví prostoru žádosti: úsek a odpovědná osoba (po zdědění z nadřazených prostorů).
/// <paramref name="Source"/> říká, odkud se hodnoty vzaly („čtečka“, „místnost M1000“, „úsek CHIR“…).
/// </summary>
public record AreaOwnership(
    int? OrgUnitId, string? OrgUnitName,
    int? ResponsibleEmployeeId, string? ResponsibleName,
    string Source)
{
    public bool IsKnown => OrgUnitId is not null || ResponsibleEmployeeId is not null;
}

/// <summary>Vztah cílového zaměstnance a prostoru žádosti.</summary>
public enum UnitRelation
{
    /// <summary>Nelze určit (prostor nemá úsek ani odpovědnou osobu, nebo položka prostor nemá).</summary>
    Unknown = 0,
    /// <summary>Prostor je ve vlastním úseku zaměstnance.</summary>
    Inside = 1,
    /// <summary>Prostor je mimo vlastní úsek zaměstnance.</summary>
    Outside = 2,
}

/// <summary>
/// Kontext položky žádosti pro podmíněné úrovně matice: kategorie cílového zaměstnance,
/// vlastnictví prostoru a vztah vlastní / cizí úsek. Parkování a kamery / EZS prostor nemají
/// (<see cref="Area"/> je null) — úrovně podmíněné úsekem se u nich neuplatní.
/// </summary>
public record ApprovalContext(EmployeeRank Rank, AreaOwnership? Area, UnitRelation Relation, string? RelationReason = null)
{
    public static ApprovalContext ForEmployeeOnly(EmployeeRank rank) => new(rank, null, UnitRelation.Unknown);

    /// <summary>Platí úroveň pro tuto položku?</summary>
    public bool Applies(ApprovalLevel level, bool treatUnknownUnitAsOutside = false)
    {
        if (level.AppliesToRanks != RankFlags.None && !level.AppliesToRanks.HasFlag(Rank.ToFlag()))
            return false;

        if (level.Scope == LevelScope.Always)
            return true;
        if (Area is null)
            return false; // položka bez prostoru (parkování, kamery) — podmínka úseku se neuplatní

        var relation = Relation;
        if (relation == UnitRelation.Unknown)
            relation = treatUnknownUnitAsOutside ? UnitRelation.Outside : UnitRelation.Inside;

        return level.Scope switch
        {
            LevelScope.InsideOwnUnit => relation == UnitRelation.Inside,
            LevelScope.OutsideOwnUnit => relation == UnitRelation.Outside,
            _ => true,
        };
    }

    public string RelationLabel => Relation switch
    {
        UnitRelation.Inside => "vlastní úsek",
        UnitRelation.Outside => "mimo vlastní úsek",
        _ => Area is null ? "bez prostoru" : "úsek prostoru neurčen",
    };
}

/// <summary>
/// Sestavuje <see cref="ApprovalContext"/>: dohledá kategorii zaměstnance, vlastnictví prostoru
/// (čtečka → místnost / chodba → patro → budova, skupina) a vztah vlastní / cizí úsek:
/// <list type="bullet">
///   <item>úsek prostoru je stejný jako úsek zaměstnance, nebo jeho předek / potomek ve stromu úseků, nebo</item>
///   <item>odpovědná osoba prostoru je zaměstnanec sám, někdo z jeho nadřízených, nebo naopak
///     zaměstnanec je v linii nadřízených odpovědné osoby (náměstek žádá o prostor svého úseku).</item>
/// </list>
/// Instance si během požadavku pamatuje načtená data (úseky, zaměstnance).
/// </summary>
public class ApprovalContextResolver(AcsDbContext db)
{
    private Dictionary<int, OrgUnit>? _units;
    private Dictionary<int, int?>? _unitParents;
    private readonly Dictionary<int, Employee?> _employees = [];
    private readonly Dictionary<int, ApprovalContext> _byItem = [];

    /// <summary>Kontext položky (čtečka / skupina / parkování / kamery) — cílový zaměstnanec z žádosti.</summary>
    public async Task<ApprovalContext> ForItemAsync(AccessRequestItem item, CancellationToken ct = default)
    {
        if (item.Id != 0 && _byItem.TryGetValue(item.Id, out var cached))
            return cached;

        var targetEmployeeId = item.Request?.TargetEmployeeId
            ?? await db.AccessRequests.Where(r => r.Id == item.RequestId).Select(r => r.TargetEmployeeId).FirstOrDefaultAsync(ct);

        ApprovalContext context;
        if (item.ReaderId is { } readerId)
            context = await ForReaderAsync(targetEmployeeId, readerId, ct);
        else if (item.ReaderGroupId is { } groupId)
            context = await ForGroupAsync(targetEmployeeId, groupId, ct);
        else
            context = await ForEmployeeAsync(targetEmployeeId, ct);

        if (item.Id != 0)
            _byItem[item.Id] = context;
        return context;
    }

    public async Task<ApprovalContext> ForEmployeeAsync(int employeeId, CancellationToken ct = default)
    {
        var employee = await GetEmployeeAsync(employeeId, ct);
        return ApprovalContext.ForEmployeeOnly(employee?.EffectiveRank ?? EmployeeRank.Staff);
    }

    public async Task<ApprovalContext> ForReaderAsync(int employeeId, int readerId, CancellationToken ct = default)
    {
        var area = await ResolveReaderOwnershipAsync(readerId, ct);
        return await BuildAsync(employeeId, area, ct);
    }

    public async Task<ApprovalContext> ForGroupAsync(int employeeId, int groupId, CancellationToken ct = default)
    {
        var area = await ResolveGroupOwnershipAsync(groupId, ct);
        return await BuildAsync(employeeId, area, ct);
    }

    private async Task<ApprovalContext> BuildAsync(int employeeId, AreaOwnership area, CancellationToken ct)
    {
        var employee = await GetEmployeeAsync(employeeId, ct);
        var rank = employee?.EffectiveRank ?? EmployeeRank.Staff;
        if (employee is null)
            return new ApprovalContext(rank, area, UnitRelation.Unknown, "cílový zaměstnanec nenalezen");

        var (relation, reason) = await RelationAsync(employee, area, ct);
        return new ApprovalContext(rank, area, relation, reason);
    }

    /// <summary>Vlastnictví čtečky: čtečka → místnost / chodba → patro → budova.</summary>
    public async Task<AreaOwnership> ResolveReaderOwnershipAsync(int readerId, CancellationToken ct = default)
    {
        var reader = await db.Readers.AsNoTracking()
            .Include(r => r.Room!).ThenInclude(room => room.Floor!).ThenInclude(f => f.Building)
            .Include(r => r.Corridor!).ThenInclude(c => c.Floor!).ThenInclude(f => f.Building)
            .FirstOrDefaultAsync(r => r.Id == readerId, ct);
        if (reader is null)
            return new AreaOwnership(null, null, null, null, "čtečka nenalezena");

        var floor = reader.Room?.Floor ?? reader.Corridor?.Floor;
        var chain = new List<(IOwnedArea Area, string Label)> { (reader, $"čtečka {reader.Name}") };
        if (reader.Room is { } room)
            chain.Add((room, $"místnost {room.Name}"));
        if (reader.Corridor is { } corridor)
            chain.Add((corridor, $"chodba {corridor.Name}"));
        if (floor is not null)
            chain.Add((floor, $"patro {floor.Name}"));
        if (floor?.Building is { } building)
            chain.Add((building, $"budova {building.Name}"));

        return await FromChainAsync(chain, ct);
    }

    /// <summary>Vlastnictví skupiny čteček (skupina sama; bez vlastníka se nedědí ze čteček).</summary>
    public async Task<AreaOwnership> ResolveGroupOwnershipAsync(int groupId, CancellationToken ct = default)
    {
        var group = await db.ReaderGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
            return new AreaOwnership(null, null, null, null, "skupina nenalezena");
        return await FromChainAsync([(group, $"skupina {group.Name}")], ct);
    }

    /// <summary>
    /// První prostor v řetězu s odpovědnou osobou dává osobu; první s úsekem dává úsek. Bez
    /// odpovědné osoby se vezme vedoucí úseku (směrem nahoru stromem úseků).
    /// </summary>
    private async Task<AreaOwnership> FromChainAsync(IReadOnlyList<(IOwnedArea Area, string Label)> chain, CancellationToken ct)
    {
        int? unitId = null;
        string? unitSource = null;
        int? ownerId = null;
        string? ownerSource = null;
        foreach (var (area, label) in chain)
        {
            if (unitId is null && area.OrgUnitId is not null)
            {
                unitId = area.OrgUnitId;
                unitSource = label;
            }

            if (ownerId is null && area.ResponsibleEmployeeId is not null)
            {
                ownerId = area.ResponsibleEmployeeId;
                ownerSource = label;
            }
        }

        var units = await GetUnitsAsync(ct);
        string? unitName = null;
        if (unitId is { } uid && units.TryGetValue(uid, out var unit))
        {
            unitName = unit.DisplayName;
            if (ownerId is null)
            {
                // vedoucí úseku, případně nadřazeného úseku
                var current = unit;
                var visited = new HashSet<int>();
                while (current is not null && visited.Add(current.Id))
                {
                    if (current.HeadEmployeeId is { } head)
                    {
                        ownerId = head;
                        ownerSource = $"vedoucí úseku {current.DisplayName}";
                        break;
                    }

                    current = current.ParentId is { } pid && units.TryGetValue(pid, out var parent) ? parent : null;
                }
            }
        }

        string? ownerName = null;
        if (ownerId is { } oid)
        {
            var owner = await GetEmployeeAsync(oid, ct);
            if (owner is null || !owner.IsActive)
            {
                ownerId = null;
                ownerSource = null;
            }
            else
            {
                ownerName = owner.FullName;
            }
        }

        var source = string.Join(", ", new[] { unitSource is null ? null : $"úsek: {unitSource}", ownerSource is null ? null : $"odpovědná osoba: {ownerSource}" }
            .Where(s => s is not null));
        return new AreaOwnership(unitId, unitName, ownerId, ownerName, source.Length == 0 ? "prostor bez úseku a odpovědné osoby" : source);
    }

    /// <summary>Vztah zaměstnance a prostoru (viz popis třídy) s vysvětlením pro zobrazení.</summary>
    public async Task<(UnitRelation Relation, string? Reason)> RelationAsync(Employee employee, AreaOwnership area, CancellationToken ct = default)
    {
        if (!area.IsKnown)
            return (UnitRelation.Unknown, "prostor nemá určený úsek ani odpovědnou osobu");

        // 1) Stejná linie úseků.
        if (area.OrgUnitId is { } areaUnit && employee.OrgUnitId is { } employeeUnit)
        {
            var parents = await GetUnitParentsAsync(ct);
            var areaLine = OrgStructureService.AncestorsAndSelf(areaUnit, parents);
            var employeeLine = OrgStructureService.AncestorsAndSelf(employeeUnit, parents);
            if (areaLine.Contains(employeeUnit) || employeeLine.Contains(areaUnit))
                return (UnitRelation.Inside, areaUnit == employeeUnit ? "stejný úsek" : "úsek prostoru je v linii úseku zaměstnance");
        }

        // 2) Odpovědná osoba v linii nadřízených zaměstnance, nebo zaměstnanec v linii odpovědné osoby.
        if (area.ResponsibleEmployeeId is { } ownerId)
        {
            if (ownerId == employee.Id)
                return (UnitRelation.Inside, "zaměstnanec je odpovědnou osobou prostoru");
            var employeeChain = await ManagerChainAsync(employee.Id, ct);
            if (employeeChain.Contains(ownerId))
                return (UnitRelation.Inside, "odpovědná osoba prostoru je nadřízeným zaměstnance");
            var ownerChain = await ManagerChainAsync(ownerId, ct);
            if (ownerChain.Contains(employee.Id))
                return (UnitRelation.Inside, "zaměstnanec řídí úsek, kterému prostor patří");
        }

        // Úsek prostoru známý, ale zaměstnanec bez úseku a mimo linii → cizí úsek.
        return (UnitRelation.Outside, area.OrgUnitId is not null && employee.OrgUnitId is null
            ? "zaměstnanec nemá přiřazený úsek"
            : null);
    }

    /// <summary>Odpovědná osoba prostoru jako zaměstnanec (pro schvalovatele typu „odpovědná osoba úseku“).</summary>
    public Task<Employee?> GetEmployeeAsync(int id, CancellationToken ct = default)
        => GetEmployeeCoreAsync(id, ct);

    private async Task<Employee?> GetEmployeeCoreAsync(int id, CancellationToken ct)
    {
        if (_employees.TryGetValue(id, out var cached))
            return cached;
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        _employees[id] = employee;
        return employee;
    }

    /// <summary>Id všech nadřízených (bez zaměstnance samého), s pojistkou proti cyklu.</summary>
    private async Task<HashSet<int>> ManagerChainAsync(int employeeId, CancellationToken ct)
    {
        var chain = new HashSet<int>();
        var current = await GetEmployeeCoreAsync(employeeId, ct);
        var guard = 0;
        while (current?.ManagerId is { } managerId && guard++ < 50 && chain.Add(managerId))
            current = await GetEmployeeCoreAsync(managerId, ct);
        return chain;
    }

    private async Task<Dictionary<int, OrgUnit>> GetUnitsAsync(CancellationToken ct)
        => _units ??= await db.OrgUnits.AsNoTracking().ToDictionaryAsync(u => u.Id, ct);

    private async Task<Dictionary<int, int?>> GetUnitParentsAsync(CancellationToken ct)
        => _unitParents ??= (await GetUnitsAsync(ct)).Values.ToDictionary(u => u.Id, u => u.ParentId);
}
