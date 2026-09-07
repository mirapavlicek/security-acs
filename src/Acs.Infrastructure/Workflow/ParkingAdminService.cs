using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Integration;
using Acs.Infrastructure.Notifications;
using Acs.Infrastructure.Pdf;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Workflow;

/// <summary>
/// Fronta správce parkování: schválená parkovací povolení se vydávají (číslo povolení,
/// kartička za sklo, zápis SPZ mezi identifikátory zaměstnance) a odebírají.
/// Obraz <see cref="CardAdminService"/> pro parkování. SPZ jsou k dispozici jako
/// <see cref="EmployeeIdentifier"/> typu <see cref="IdentifierType.LicensePlate"/> (online
/// autorizace u brány přes integrační API) a po vydání / odebrání se stav volitelně předá
/// konektoru parkovacího systému (<see cref="ParkingProvisioningService"/>).
/// </summary>
public class ParkingAdminService(AcsDbContext db, AuditService audit, INotificationService? notifier = null,
    ParkingProvisioningService? provisioning = null)
{
    /// <summary>Předá stav zaměstnance do parkovacího systému, je-li předávání zapnuté (chyba se zapíše k povolení).</summary>
    public async Task<ParkingProvisioningResult> PushToParkingSystemAsync(int employeeId, string? userName,
        CancellationToken ct = default)
        => provisioning is null
            ? new ParkingProvisioningResult(false, false, "předávání do parkovacího systému není k dispozici")
            : await provisioning.SyncEmployeeAsync(employeeId, userName, ct);

    /// <summary>Schválené parkovací položky (udělení i odebrání) čekající na správce parkování.</summary>
    public Task<List<AccessRequestItem>> GetQueueAsync(CancellationToken ct = default)
        => ParkingItems()
            .Where(i => i.Status == RequestStatus.Approved)
            .OrderBy(i => i.Request!.CreatedAt)
            .ToListAsync(ct);

    /// <summary>Vydaná (platná) povolení, volitelně filtrovaná fulltextem (jméno, SPZ, číslo, druh).</summary>
    public async Task<List<AccessRequestItem>> GetIssuedAsync(string? search = null, CancellationToken ct = default)
    {
        var items = await ParkingItems()
            .Where(i => i.Status == RequestStatus.Issued && i.Request!.Kind == RequestKind.Grant)
            .OrderByDescending(i => i.ParkingPermit!.IssuedAt)
            .ToListAsync(ct);

        if (string.IsNullOrWhiteSpace(search))
            return items;

        var needle = search.Trim();
        var plate = EmployeeIdentifier.Normalize(needle);
        return items.Where(i =>
            i.Request!.TargetEmployee!.FullName.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || (i.ParkingPermit!.PermitNumber?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
            || (i.ParkingPermit.PermitType?.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
            || (i.ParkingPermit.FunctionTitle?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
            || i.ParkingPermit.Plates.Any(p => p.Value.Contains(plate, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>Načte položku parkovacího povolení se všemi navigacemi potřebnými pro zobrazení a tisk.</summary>
    public Task<AccessRequestItem?> GetItemAsync(int itemId, CancellationToken ct = default)
        => ParkingItems().FirstOrDefaultAsync(i => i.Id == itemId, ct);

    /// <summary>Položka udělení (grant) daného povolení.</summary>
    public Task<AccessRequestItem?> GetGrantItemAsync(int permitId, CancellationToken ct = default)
        => ParkingItems()
            .Where(i => i.ParkingPermitId == permitId && i.Request!.Kind == RequestKind.Grant)
            .OrderByDescending(i => i.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Vydá schválené povolení: přidělí číslo (nebo použije zadané), zapíše SPZ jako
    /// identifikátory zaměstnance s platností povolení a položku označí jako vydanou.
    /// </summary>
    public async Task IssueAsync(int itemId, int userId, string? permitNumber, string? userName,
        CancellationToken ct = default)
        => await IssueAsync(itemId, userId, permitNumber, userName, parkingSpotId: null, ct);

    /// <summary>
    /// Vydá schválené povolení a volitelně ho rovnou přiřadí na vyhrazené parkovací místo
    /// (<paramref name="parkingSpotId"/>); místo musí ležet v některém z areálů povolení.
    /// </summary>
    public async Task IssueAsync(int itemId, int userId, string? permitNumber, string? userName,
        int? parkingSpotId, CancellationToken ct = default)
    {
        var item = await ParkingItems().FirstOrDefaultAsync(i => i.Id == itemId, ct)
            ?? throw new KeyNotFoundException("Položka nenalezena.");
        if (item.Status != RequestStatus.Approved || item.Request!.Kind != RequestKind.Grant)
            throw new InvalidOperationException("Položka není schválené udělení parkovacího povolení.");

        var permit = item.ParkingPermit!;
        var now = DateTime.UtcNow;

        var number = string.IsNullOrWhiteSpace(permitNumber)
            ? await NextPermitNumberAsync(now, ct)
            : permitNumber.Trim();
        if (await db.ParkingPermits.AnyAsync(p => p.Id != permit.Id && p.PermitNumber == number && p.RevokedAt == null, ct))
            throw new InvalidOperationException($"Číslo povolení „{number}“ už je použité.");

        if (parkingSpotId is { } spotId)
            permit.ParkingSpot = await ResolveSpotAsync(permit, spotId, ct);
        permit.ParkingSpotId = parkingSpotId;

        permit.PermitNumber = number;
        permit.IssuedAt = now;
        permit.IssuedByUserId = userId;

        foreach (var plate in permit.Plates)
        {
            var identifier = await db.EmployeeIdentifiers.FirstOrDefaultAsync(x =>
                x.EmployeeId == permit.EmployeeId && x.Type == IdentifierType.LicensePlate && x.Value == plate.Value, ct);
            if (identifier is null)
            {
                identifier = new EmployeeIdentifier
                {
                    EmployeeId = permit.EmployeeId,
                    Type = IdentifierType.LicensePlate,
                    Value = plate.Value,
                    Source = RecordSource.Manual,
                };
                db.EmployeeIdentifiers.Add(identifier);
            }

            identifier.IsActive = true;
            identifier.ValidFrom = permit.ValidFrom;
            identifier.ValidTo = permit.ValidTo;
            identifier.Note = $"parkovací povolení {number}";
            plate.EmployeeIdentifier = identifier;
        }

        item.Status = RequestStatus.Issued;
        item.PushedAt = now;
        item.PushResult = $"vydáno povolení č. {number}";
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(userName, "parking-permit-issued", "ParkingPermit", permit.Id.ToString(),
            $"č. {number}, {permit.SubjectText()}"
            + (permit.ParkingSpot is null ? "" : $", místo {permit.ParkingSpot.DisplayName()}"), ct);
        if (notifier is not null)
            await notifier.NotifyDecidedAsync(item.Id, ct);
        if (provisioning is not null)
            await provisioning.SyncEmployeeAsync(permit.EmployeeId, userName, ct);
    }

    /// <summary>
    /// Přiřadí vydanému povolení vyhrazené místo, nebo ho odebere (<paramref name="parkingSpotId"/> = null).
    /// Cedule na místě se generuje z aktuálních povolení, takže po změně stačí ceduli znovu vytisknout.
    /// </summary>
    public async Task AssignSpotAsync(int permitId, int? parkingSpotId, string? userName, CancellationToken ct = default)
    {
        var permit = await db.ParkingPermits
            .Include(p => p.Sites).ThenInclude(s => s.Site)
            .Include(p => p.ParkingSpot)
            .FirstOrDefaultAsync(p => p.Id == permitId, ct)
            ?? throw new KeyNotFoundException("Parkovací povolení nenalezeno.");
        if (permit.IssuedAt is null || permit.RevokedAt is not null)
            throw new InvalidOperationException("Místo lze přiřadit jen vydanému (neodebranému) povolení.");

        var previous = permit.ParkingSpot?.DisplayName();
        permit.ParkingSpot = parkingSpotId is { } spotId ? await ResolveSpotAsync(permit, spotId, ct) : null;
        permit.ParkingSpotId = parkingSpotId;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(userName, "parking-spot-assigned", "ParkingPermit", permitId.ToString(),
            $"{previous ?? "bez místa"} → {permit.ParkingSpot?.DisplayName() ?? "bez místa"}", ct);
    }

    /// <summary>Načte místo a ověří, že je aktivní a leží v areálu, pro který povolení platí.</summary>
    private async Task<ParkingSpot> ResolveSpotAsync(ParkingPermit permit, int spotId, CancellationToken ct)
    {
        var spot = await db.ParkingSpots.Include(s => s.Site).FirstOrDefaultAsync(s => s.Id == spotId, ct)
            ?? throw new KeyNotFoundException("Parkovací místo nenalezeno.");
        if (!spot.IsActive)
            throw new InvalidOperationException($"Parkovací místo {spot.DisplayName()} je neaktivní.");
        if (!permit.AllSites && permit.Sites.All(s => s.SiteId != spot.SiteId))
            throw new InvalidOperationException(
                $"Parkovací místo {spot.DisplayName()} leží mimo areály povolení ({permit.SitesText()}).");
        return spot;
    }

    /// <summary>
    /// Data pro ceduli A4 na konkrétní místo: SPZ (nebo funkce) všech povolení na místě, která jsou
    /// vydaná, neodebraná a platná k dnešnímu dni. Čekající, zamítnutá, odebraná a expirovaná se neuvádějí.
    /// </summary>
    public async Task<ParkingSpotSignView?> GetSpotSignAsync(int spotId, CancellationToken ct = default)
    {
        var spot = await db.ParkingSpots.Include(s => s.Site).FirstOrDefaultAsync(s => s.Id == spotId, ct);
        return spot is null ? null : await BuildSignAsync(spot, ct);
    }

    /// <summary>Cedule všech aktivních míst areálu (hromadný tisk, jedno místo = jedna stránka).</summary>
    public async Task<List<ParkingSpotSignView>> GetSiteSignsAsync(int siteId, CancellationToken ct = default)
    {
        var spots = await db.ParkingSpots.Include(s => s.Site)
            .Where(s => s.SiteId == siteId && s.IsActive)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Code)
            .ToListAsync(ct);
        var signs = new List<ParkingSpotSignView>(spots.Count);
        foreach (var spot in spots)
            signs.Add(await BuildSignAsync(spot, ct));
        return signs;
    }

    /// <summary>Povolení, která se na ceduli místa objeví (vydaná, neodebraná, platná).</summary>
    public Task<List<ParkingPermit>> GetSpotPermitsAsync(int spotId, CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        return db.ParkingPermits
            .Include(p => p.Employee)
            .Include(p => p.PermitType)
            .Include(p => p.Plates)
            .Where(p => p.ParkingSpotId == spotId && p.IssuedAt != null && p.RevokedAt == null
                        && (p.ValidTo == null || p.ValidTo >= today))
            .OrderBy(p => p.PermitNumber)
            .ToListAsync(ct);
    }

    private async Task<ParkingSpotSignView> BuildSignAsync(ParkingSpot spot, CancellationToken ct)
    {
        var permits = await GetSpotPermitsAsync(spot.Id, ct);
        return ParkingSpotSignView.For(spot, permits);
    }

    /// <summary>Správce parkování provede odebrání, o které bylo požádáno (položka Revoke ve frontě).</summary>
    public async Task ConfirmRevokeAsync(int itemId, string? reason, string? userName, CancellationToken ct = default)
    {
        var item = await ParkingItems().FirstOrDefaultAsync(i => i.Id == itemId, ct)
            ?? throw new KeyNotFoundException("Položka nenalezena.");
        if (item.Status != RequestStatus.Approved || item.Request!.Kind != RequestKind.Revoke)
            throw new InvalidOperationException("Položka není schválené odebrání parkovacího povolení.");

        var now = DateTime.UtcNow;
        item.Status = RequestStatus.Revoked;
        item.PushedAt = now;
        item.PushResult = "odebráno";
        await RevokeCoreAsync(item.ParkingPermit!, reason ?? item.Request.Justification, now, ct);

        await db.SaveChangesAsync(ct);
        await audit.LogAsync(userName, "parking-permit-revoked", "ParkingPermit",
            item.ParkingPermitId!.Value.ToString(), reason, ct);
        if (notifier is not null)
            await notifier.NotifyDecidedAsync(item.Id, ct);
        if (provisioning is not null)
            await provisioning.SyncEmployeeAsync(item.ParkingPermit!.EmployeeId, userName, ct);
    }

    /// <summary>
    /// Přímé odebrání vydaného povolení (správce parkování, nebo automatizace při
    /// expiraci / odchodu zaměstnance). Kvůli auditní stopě založí revokační žádost
    /// rovnou ve stavu „odebráno“.
    /// </summary>
    public async Task RevokeAsync(int permitId, int requesterUserId, string reason, string? userName,
        string auditAction = "parking-permit-revoked", CancellationToken ct = default)
    {
        var permit = await db.ParkingPermits
            .Include(p => p.Plates).ThenInclude(pl => pl.EmployeeIdentifier)
            .FirstOrDefaultAsync(p => p.Id == permitId, ct)
            ?? throw new KeyNotFoundException("Parkovací povolení nenalezeno.");

        var issued = await db.AccessRequestItems.AnyAsync(i =>
            i.ParkingPermitId == permitId && i.Request!.Kind == RequestKind.Grant
            && i.Status == RequestStatus.Issued, ct);
        if (!issued)
            throw new InvalidOperationException("Povolení není vydané — není co odebírat.");

        var now = DateTime.UtcNow;

        // Případná čekající žádost o odebrání se tím vyřídí.
        var pendingRevokes = await db.AccessRequestItems
            .Where(i => i.ParkingPermitId == permitId && i.Request!.Kind == RequestKind.Revoke
                        && (i.Status == RequestStatus.Pending || i.Status == RequestStatus.Approved))
            .ToListAsync(ct);
        foreach (var pending in pendingRevokes)
        {
            pending.Status = RequestStatus.Revoked;
            pending.PushedAt = now;
            pending.PushResult = reason;
        }

        var revokeItem = new AccessRequestItem
        {
            ParkingPermitId = permitId,
            Status = RequestStatus.Revoked,
            CurrentLevelOrder = 0,
            DecidedAt = now,
            PushedAt = now,
            PushResult = reason,
        };
        db.AccessRequests.Add(new AccessRequest
        {
            Kind = RequestKind.Revoke,
            RequesterUserId = requesterUserId,
            TargetEmployeeId = permit.EmployeeId,
            Justification = reason,
            Items = [revokeItem],
        });

        await RevokeCoreAsync(permit, reason, now, ct);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(userName, auditAction, "ParkingPermit", permitId.ToString(), reason, ct);
        if (notifier is not null)
            await notifier.NotifyDecidedAsync(revokeItem.Id, ct);
        if (provisioning is not null)
            await provisioning.SyncEmployeeAsync(permit.EmployeeId, userName, ct);
    }

    /// <summary>Označí udělení jako odebrané a deaktivuje SPZ identifikátory založené při vydání.</summary>
    private async Task RevokeCoreAsync(ParkingPermit permit, string? reason, DateTime now, CancellationToken ct)
    {
        permit.RevokedAt = now;
        permit.RevokeReason = reason;

        var grants = await db.AccessRequestItems
            .Where(i => i.ParkingPermitId == permit.Id && i.Request!.Kind == RequestKind.Grant
                        && i.Status == RequestStatus.Issued)
            .ToListAsync(ct);
        foreach (var grant in grants)
            grant.Status = RequestStatus.Revoked;

        // Volající načítá Plates včetně EmployeeIdentifier (viz ParkingItems / RevokeAsync).
        foreach (var plate in permit.Plates)
        {
            if (plate.EmployeeIdentifier is { } identifier)
            {
                identifier.IsActive = false;
                identifier.ValidTo = now;
            }
        }
    }

    /// <summary>Číslo povolení ve tvaru P-RRRR-NNNN, v rámci roku vzestupně.</summary>
    private async Task<string> NextPermitNumberAsync(DateTime now, CancellationToken ct)
    {
        var prefix = $"P-{now.Year}-";
        var existing = await db.ParkingPermits
            .Where(p => p.PermitNumber != null && p.PermitNumber.StartsWith(prefix))
            .Select(p => p.PermitNumber!)
            .ToListAsync(ct);
        var max = existing
            .Select(n => int.TryParse(n[prefix.Length..], out var v) ? v : 0)
            .DefaultIfEmpty(0)
            .Max();
        return $"{prefix}{max + 1:0000}";
    }

    private IQueryable<AccessRequestItem> ParkingItems()
        => db.AccessRequestItems
            .Include(i => i.Request!).ThenInclude(r => r.TargetEmployee)
            .Include(i => i.Request!).ThenInclude(r => r.RequesterUser)
            .Include(i => i.ParkingPermit!).ThenInclude(p => p.PermitType)
            .Include(i => i.ParkingPermit!).ThenInclude(p => p.Plates).ThenInclude(pl => pl.EmployeeIdentifier)
            .Include(i => i.ParkingPermit!).ThenInclude(p => p.Sites).ThenInclude(s => s.Site)
            .Include(i => i.ParkingPermit!).ThenInclude(p => p.IssuedByUser)
            .Include(i => i.ParkingPermit!).ThenInclude(p => p.ParkingSpot!).ThenInclude(s => s.Site)
            .Include(i => i.Stages)
            .Include(i => i.Decisions).ThenInclude(d => d.ApproverUser)
            .Where(i => i.ParkingPermitId != null);
}
