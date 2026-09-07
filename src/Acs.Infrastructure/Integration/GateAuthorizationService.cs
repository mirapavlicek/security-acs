using System.Text.Json;
using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Integration;

/// <summary>
/// Online autorizace u vjezdu (vzor C v <c>docs/integrace/README.md</c>): parkovací systém
/// (GreenCenter) pošle načtenou SPZ / kartu a id vjezdu, ACS odpoví povolit / zamítnout
/// s důvodem. Rozhoduje se výhradně podle vydaných parkovacích povolení — SPZ musí být
/// platný identifikátor zaměstnance a povolení musí platit pro areál vjezdu. Přibližné
/// porovnání značek se záměrně nedělá (pustilo by cizí vozidlo).
/// Každé rozhodnutí se ukládá jako <see cref="IntegrationEvent"/> typu
/// <c>authorizationCheck</c>, aby bylo dohledatelné, proč brána někoho (ne)pustila.
/// </summary>
public class GateAuthorizationService(AcsDbContext db, SettingsService settings)
{
    public const string CheckEventType = "authorizationCheck";
    private const int DefaultCacheTtlSeconds = 300;

    public async Task<AuthorizationDecision> DecideAsync(AuthorizationRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var traceId = Guid.NewGuid().ToString("N");
        var direction = string.Equals(request.Direction, "out", StringComparison.OrdinalIgnoreCase) ? "out" : "in";
        var cacheTtl = await settings.GetIntAsync(SettingKeys.ParkingSystemCacheTtlSeconds, DefaultCacheTtlSeconds, ct);
        var exitAlwaysAllowed = await settings.GetBoolAsync(SettingKeys.ParkingSystemExitAlwaysAllowed, true, ct);

        var credentialType = IntegrationContract.ParseCredentialType(request.Credential.Type);
        var value = EmployeeIdentifier.Normalize(request.Credential.Value ?? "");

        var site = await ResolveSiteAsync(request.AccessPointId, ct);
        var evaluation = await EvaluateAsync(site, credentialType, value, now, ct);

        var decision = evaluation.Reason == DecisionReason.Allowed ? "allow" : "deny";
        var reason = evaluation.Reason;
        string? exitOverride = null;
        if (decision == "deny" && direction == "out" && exitAlwaysAllowed)
        {
            // Výjezd: vozidlo nesmí zůstat zavřené v areálu — pustíme, ale původní důvod zůstane v záznamu.
            exitOverride = reason;
            decision = "allow";
            reason = DecisionReason.Allowed;
        }

        var result = new AuthorizationDecision(
            decision, reason,
            evaluation.Employee?.Id.ToString(),
            decision == "allow" ? evaluation.Employee?.FullName : null,
            evaluation.Permit is null ? null : $"permit-{evaluation.Permit.Id}",
            evaluation.Permit?.ValidTo,
            decision == "allow" ? cacheTtl : 0,
            now, traceId);

        db.IntegrationEvents.Add(new IntegrationEvent
        {
            Source = IntegrationContract.ParkingSource,
            Type = CheckEventType,
            OccurredAt = request.OccurredAt ?? now,
            ReceivedAt = now,
            AccessPointId = request.AccessPointId,
            SiteId = site?.Id,
            EmployeeId = evaluation.Employee?.Id,
            CredentialType = credentialType,
            CredentialValue = value.Length == 0 ? null : value,
            Direction = direction,
            Decision = decision,
            DecisionReason = reason,
            Confidence = request.Confidence,
            TraceId = traceId,
            Details = exitOverride is null && evaluation.Permit is null
                ? null
                : JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["exitOverride"] = exitOverride,
                    ["permitNumber"] = evaluation.Permit?.PermitNumber,
                }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!)),
        });
        await db.SaveChangesAsync(ct);

        return result;
    }

    /// <summary>
    /// Najde areál podle id přístupového bodu: <c>site:MOT</c>, kód areálu, nebo některý
    /// z identifikátorů vjezdů v <see cref="Site.GateExternalIds"/>.
    /// </summary>
    public async Task<Site?> ResolveSiteAsync(string? accessPointId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessPointId))
            return null;

        var id = accessPointId.Trim();
        var sites = await db.Sites.AsNoTracking().Where(s => s.IsActive).ToListAsync(ct);
        return sites.FirstOrDefault(s => s.GateExternalIdList().Any(g => g.Equals(id, StringComparison.OrdinalIgnoreCase)))
               ?? sites.FirstOrDefault(s => s.IntegrationId().Equals(id, StringComparison.OrdinalIgnoreCase))
               ?? sites.FirstOrDefault(s => s.Code is not null && s.Code.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record Evaluation(string Reason, Employee? Employee = null, ParkingPermit? Permit = null);

    private async Task<Evaluation> EvaluateAsync(Site? site, IdentifierType? credentialType, string value,
        DateTime now, CancellationToken ct)
    {
        if (site is null)
            return new Evaluation(DecisionReason.AccessPointUnknown);
        if (credentialType is null || value.Length == 0)
            return new Evaluation(DecisionReason.CredentialUnknown);

        var identifiers = await db.EmployeeIdentifiers.AsNoTracking()
            .Include(i => i.Employee)
            .Where(i => i.Type == credentialType && i.Value == value)
            .ToListAsync(ct);
        if (identifiers.Count == 0)
            return new Evaluation(DecisionReason.CredentialUnknown);

        var valid = identifiers.Where(i => i.IsValidAt(now)).ToList();
        if (valid.Count == 0)
            return new Evaluation(DecisionReason.CredentialExpired, identifiers[0].Employee);

        var employees = valid.Select(i => i.Employee!).DistinctBy(e => e.Id).ToList();
        var active = employees.Where(e => e.IsActive).ToList();
        if (active.Count == 0)
            return new Evaluation(DecisionReason.PersonEnded, employees[0]);

        var employeeIds = active.Select(e => e.Id).ToList();
        var permits = await db.ParkingPermits.AsNoTracking()
            .Include(p => p.Sites)
            .Include(p => p.Plates)
            .Where(p => employeeIds.Contains(p.EmployeeId) && p.IssuedAt != null && p.RevokedAt == null)
            .ToListAsync(ct);

        // Povolení vázané na SPZ platí jen pro své značky; povolení na funkci je přenosné
        // mezi vozidly (identifikuje osobu), stejně jako průjezd na kartu.
        bool AppliesTo(ParkingPermit p) =>
            credentialType != IdentifierType.LicensePlate
            || p.Plates.Count == 0
            || p.Plates.Any(pl => pl.Value == value);

        var applicable = permits.Where(AppliesTo).ToList();
        var covering = applicable.Where(p => p.CoversSite(site.Id)).ToList();
        if (covering.Count == 0)
            return new Evaluation(DecisionReason.NoEntitlement, active[0]);

        var current = covering.Where(p => p.IsValidAt(now))
            .OrderByDescending(p => p.ValidTo is null)
            .ThenByDescending(p => p.ValidTo)
            .FirstOrDefault();
        if (current is null)
            return new Evaluation(DecisionReason.EntitlementExpired, active[0], covering[0]);

        var owner = active.First(e => e.Id == current.EmployeeId);
        return new Evaluation(DecisionReason.Allowed, owner, current);
    }
}
