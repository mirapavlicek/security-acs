using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Integration;

/// <summary>Výsledek předání stavu zaměstnance do parkovacího systému.</summary>
public record ParkingProvisioningResult(bool Attempted, bool Success, string Message);

/// <summary>
/// Předání stavu do parkovacího systému (vzor A): po vydání nebo odebrání povolení pošle
/// konektoru osobu, **celý** platný seznam jejích SPZ / karet a **celý** seznam platných
/// povolení (cíl = areál). Posílá se úplný stav, ne přírůstek — konektor zneplatní, co
/// v seznamu není, takže se stavy nerozejdou ani po neúspěšném dílčím volání.
/// Chyba se zapíše k povolení (<see cref="ParkingPermit.ParkingSystemSyncError"/>) a do
/// auditu; vydání povolení v ACS kvůli ní neselže — správce vidí stav ve frontě a může
/// předání zopakovat.
/// </summary>
public class ParkingProvisioningService(AcsDbContext db, ParkingConnectorClient connector, SettingsService settings,
    AuditService audit)
{
    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
        => await settings.GetBoolAsync(SettingKeys.ParkingSystemPushEnabled, false, ct)
           && await connector.IsConfiguredAsync(ct);

    /// <summary>Předá stav zaměstnance; nikdy nevyhazuje — výsledek je v návratové hodnotě a u povolení.</summary>
    public async Task<ParkingProvisioningResult> SyncEmployeeAsync(int employeeId, string? userName,
        CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(ct))
            return new ParkingProvisioningResult(false, false, "předávání do parkovacího systému není zapnuté");

        var employee = await db.Employees.Include(e => e.Identifiers).FirstOrDefaultAsync(e => e.Id == employeeId, ct);
        if (employee is null)
            return new ParkingProvisioningResult(false, false, "zaměstnanec nenalezen");

        var now = DateTime.UtcNow;
        var permits = await db.ParkingPermits
            .Include(p => p.Sites).ThenInclude(s => s.Site)
            .Where(p => p.EmployeeId == employeeId && p.IssuedAt != null)
            .ToListAsync(ct);
        var current = permits.Where(p => p.IsValidAt(now)).ToList();

        try
        {
            var personId = employee.Id.ToString();
            var upsert = await connector.UpsertPersonAsync(personId, new ConnectorPersonUpsert(
                employee.PersonalNumber, employee.FirstName, employee.LastName, employee.Department,
                employee.IsActive ? "active" : "ended"), ct);
            if (upsert?.TargetId is { Length: > 0 } targetId && targetId != employee.ParkingSystemId)
                employee.ParkingSystemId = targetId;

            var credentials = employee.Identifiers
                .Where(i => i.Type is IdentifierType.LicensePlate or IdentifierType.Card && i.IsValidAt(now))
                .Select(i => new ConnectorCredential(IntegrationContract.CredentialTypeName(i.Type), i.Value, i.ValidFrom, i.ValidTo))
                .ToList();
            await connector.SetCredentialsAsync(personId, credentials, ct);

            var entitlements = current.SelectMany(p => p.AllSites
                    ? [new ConnectorEntitlement(null, IntegrationContract.AllSitesGroupId, p.ValidFrom, p.ValidTo, $"permit-{p.Id}")]
                    : p.Sites.Select(s => new ConnectorEntitlement(null,
                        s.Site?.IntegrationId() ?? $"site:#{s.SiteId}", p.ValidFrom, p.ValidTo, $"permit-{p.Id}")))
                .ToList();
            var result = await connector.SetEntitlementsAsync(personId, entitlements, ct);

            foreach (var permit in permits)
            {
                permit.ParkingSystemSyncedAt = now;
                permit.ParkingSystemSyncError = null;
            }
            await db.SaveChangesAsync(ct);

            var message = $"{credentials.Count} identifikátorů, {entitlements.Count} oprávnění"
                          + (result?.TargetReferences is { Count: > 0 } refs ? $" → {string.Join(", ", refs)}" : "");
            await audit.LogAsync(userName, "parking-system-synced", "Employee", employeeId.ToString(), message, ct);
            return new ParkingProvisioningResult(true, true, message);
        }
        catch (Exception ex) when (ex is ParkingConnectorException or HttpRequestException or TaskCanceledException
                                       or InvalidOperationException or System.Text.Json.JsonException)
        {
            var error = ex is TaskCanceledException ? "konektor neodpověděl (timeout)" : ex.Message;
            foreach (var permit in permits.Where(p => p.IsValidAt(now) || p.RevokedAt >= now.AddDays(-1)))
                permit.ParkingSystemSyncError = error.Length > 500 ? error[..500] : error;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(userName, "parking-system-sync-failed", "Employee", employeeId.ToString(), error, ct);
            return new ParkingProvisioningResult(true, false, error);
        }
    }
}
