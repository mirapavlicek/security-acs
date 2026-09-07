using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Integration;

/// <summary>
/// Čtecí část integračního API (vzor B — systém si stav odebírá sám): osoby, jejich
/// identifikátory, oprávnění (parkovací povolení) a přístupové body (areály / vjezdy).
/// Rozsah je záměrně omezený na to, co parkovací systém potřebuje: osoby s parkovacím
/// povolením nebo SPZ, identifikátory jen SPZ a karty, bez e-mailu a umístění kanceláře.
/// </summary>
public class IntegrationCatalogService(AcsDbContext db)
{
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 500;

    private static readonly IdentifierType[] ExportedCredentialTypes = [IdentifierType.LicensePlate, IdentifierType.Card];

    public async Task<PagedResult<PersonDto>> ListPersonsAsync(DateTime? changedSince, string? cursor, int? limit,
        CancellationToken ct = default)
    {
        var pageSize = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var afterId = int.TryParse(cursor, out var c) ? c : 0;

        var query = db.Employees.AsNoTracking()
            .Where(e => e.Id > afterId)
            .Where(e => db.ParkingPermits.Any(p => p.EmployeeId == e.Id)
                        || db.EmployeeIdentifiers.Any(i => i.EmployeeId == e.Id && i.Type == IdentifierType.LicensePlate));
        if (changedSince is { } since)
            query = query.Where(e => e.LastSyncedAt == null || e.LastSyncedAt >= since
                                     || db.ParkingPermits.Any(p => p.EmployeeId == e.Id
                                         && (p.IssuedAt >= since || p.RevokedAt >= since)));

        var employees = await query.OrderBy(e => e.Id).Take(pageSize + 1).ToListAsync(ct);
        var hasMore = employees.Count > pageSize;
        if (hasMore)
            employees.RemoveAt(employees.Count - 1);

        return new PagedResult<PersonDto>(employees.Select(ToPerson).ToList(),
            hasMore ? employees[^1].Id.ToString() : null);
    }

    public async Task<PersonDto?> GetPersonAsync(int id, CancellationToken ct = default)
    {
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        return employee is null ? null : ToPerson(employee);
    }

    public async Task<List<CredentialDto>?> GetCredentialsAsync(int employeeId, CancellationToken ct = default)
    {
        if (!await db.Employees.AnyAsync(e => e.Id == employeeId, ct))
            return null;

        var identifiers = await db.EmployeeIdentifiers.AsNoTracking()
            .Where(i => i.EmployeeId == employeeId && ExportedCredentialTypes.Contains(i.Type))
            .OrderBy(i => i.Type).ThenBy(i => i.Value)
            .ToListAsync(ct);
        return identifiers.Select(ToCredential).ToList();
    }

    /// <summary>
    /// Vydaná parkovací povolení jako oprávnění: jedno na povolení a areál (u „všech
    /// areálů“ jedno s cílem <see cref="IntegrationContract.AllSitesGroupId"/>). Odebraná
    /// a prošlá se posílají se stavem <c>revoked</c> / <c>expired</c>, aby si systém
    /// záznam zneplatnil.
    /// </summary>
    public async Task<PagedResult<EntitlementDto>> ListEntitlementsAsync(DateTime? changedSince, string? personId,
        string? status, string? cursor, int? limit, CancellationToken ct = default)
    {
        var pageSize = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var afterId = int.TryParse(cursor, out var c) ? c : 0;
        var now = DateTime.UtcNow;

        var query = db.ParkingPermits.AsNoTracking()
            .Include(p => p.PermitType)
            .Include(p => p.Sites).ThenInclude(s => s.Site)
            .Include(p => p.Plates).ThenInclude(pl => pl.EmployeeIdentifier)
            .Include(p => p.ParkingSpot).ThenInclude(s => s!.Site)
            .Where(p => p.IssuedAt != null && p.Id > afterId);
        if (int.TryParse(personId, out var employeeId))
            query = query.Where(p => p.EmployeeId == employeeId);
        if (changedSince is { } since)
            query = query.Where(p => p.IssuedAt >= since || p.RevokedAt >= since
                                     || (p.ValidTo != null && p.ValidTo >= since && p.ValidTo <= now));

        var permits = await query.OrderBy(p => p.Id).Take(pageSize + 1).ToListAsync(ct);
        var hasMore = permits.Count > pageSize;
        if (hasMore)
            permits.RemoveAt(permits.Count - 1);

        var requestIds = await db.AccessRequestItems.AsNoTracking()
            .Where(i => i.ParkingPermitId != null && i.Request!.Kind == RequestKind.Grant)
            .Select(i => new { PermitId = i.ParkingPermitId!.Value, i.RequestId })
            .ToListAsync(ct);
        var requestByPermit = requestIds.GroupBy(x => x.PermitId).ToDictionary(g => g.Key, g => g.First().RequestId);

        var items = permits.SelectMany(p => ToEntitlements(p, now, requestByPermit.GetValueOrDefault(p.Id)))
            .Where(e => status is null || e.Status == status)
            .ToList();
        return new PagedResult<EntitlementDto>(items, hasMore ? permits[^1].Id.ToString() : null);
    }

    /// <summary>Areály jako přístupové body druhu <c>gate</c> (jeden na areál, vjezdy v <c>externalIds</c>).</summary>
    public async Task<List<AccessPointDto>> ListAccessPointsAsync(CancellationToken ct = default)
    {
        var sites = await db.Sites.AsNoTracking().OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync(ct);
        return sites.Select(s =>
        {
            var externalIds = new Dictionary<string, string>();
            var gates = s.GateExternalIdList();
            for (var i = 0; i < gates.Count; i++)
                externalIds[gates.Count == 1 ? "parking" : $"parking{i + 1}"] = gates[i];
            return new AccessPointDto(s.IntegrationId(), "gate", s.Name, s.Description, externalIds, s.IsActive);
        }).ToList();
    }

    private static PersonDto ToPerson(Employee e)
    {
        var externalIds = new Dictionary<string, string>();
        if (e.ExternalId is { Length: > 0 }) externalIds["hr"] = e.ExternalId;
        if (e.ParkingSystemId is { Length: > 0 }) externalIds["parking"] = e.ParkingSystemId;
        return new PersonDto(e.Id.ToString(), e.PersonalNumber, e.FirstName, e.LastName, e.Department,
            e.IsActive ? "active" : "ended", externalIds, e.LastSyncedAt);
    }

    private static CredentialDto ToCredential(EmployeeIdentifier i)
        => new(IntegrationContract.CredentialTypeName(i.Type), i.Value, i.IsActive, i.ValidFrom, i.ValidTo, i.Note);

    public static IEnumerable<EntitlementDto> ToEntitlements(ParkingPermit p, DateTime now, int? requestId)
    {
        var status = p.RevokedAt is not null ? "revoked"
            : p.ValidTo is { } to && to < now ? "expired"
            : "active";
        var credentials = p.Plates
            .Select(pl => pl.EmployeeIdentifier is { } id
                ? ToCredential(id)
                : new CredentialDto("licensePlate", pl.Value, p.RevokedAt is null, p.ValidFrom, p.ValidTo, pl.Note))
            .ToList();
        var source = new EntitlementSource(requestId is { } r ? $"req-{r}" : null, p.IssuedAt, "approvedRequest");
        var updatedAt = p.RevokedAt ?? p.IssuedAt ?? p.CreatedAt;
        var spot = p.ParkingSpot?.DisplayName();

        if (p.AllSites)
        {
            yield return new EntitlementDto($"permit-{p.Id}", p.EmployeeId.ToString(),
                new EntitlementTarget(null, IntegrationContract.AllSitesGroupId), status, p.ValidFrom, p.ValidTo,
                source, updatedAt, credentials, p.PermitNumber, p.PermitType?.Name, spot);
            yield break;
        }

        foreach (var site in p.Sites)
        {
            var target = site.Site?.IntegrationId() ?? $"site:#{site.SiteId}";
            yield return new EntitlementDto($"permit-{p.Id}/{target}", p.EmployeeId.ToString(),
                new EntitlementTarget(target, null), status, p.ValidFrom, p.ValidTo,
                source, updatedAt, credentials, p.PermitNumber, p.PermitType?.Name, spot);
        }
    }
}
