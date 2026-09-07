using System.Text.Json;
using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Integration;

/// <summary>
/// Příjem událostí z navázaného systému (<c>POST /events</c>): vjezdy, výjezdy, zamítnutí
/// u závory. Zpracování je po jednotlivých událostech — vadná událost nezahodí dávku —
/// a duplicitní <c>id</c> se tiše přijme jako <c>duplicate</c>, takže systém smí dávku
/// po výpadku poslat znovu.
/// </summary>
public class IntegrationEventService(AcsDbContext db)
{
    public const int MaxBatchSize = 500;

    private static readonly HashSet<string> KnownTypes =
    [
        "passed", "denied", "vehicleIn", "vehicleOut", "mealTaken", "mealCancelled", "deviceOffline", "other",
    ];

    public async Task<IntegrationEventBatchResult> ReportAsync(string source, IReadOnlyList<IntegrationEventInput> events,
        CancellationToken ct = default)
    {
        var results = new List<IntegrationEventResult>(events.Count);
        var ids = events.Select(e => e.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        var existing = (await db.IntegrationEvents
                .Where(e => e.Source == source && e.ExternalId != null && ids.Contains(e.ExternalId))
                .Select(e => e.ExternalId!)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var sites = await db.Sites.AsNoTracking().Where(s => s.IsActive).ToListAsync(ct);
        var now = DateTime.UtcNow;
        int accepted = 0, rejected = 0, duplicates = 0;

        foreach (var input in events)
        {
            var problem = Validate(input);
            if (problem is not null)
            {
                rejected++;
                results.Add(new IntegrationEventResult(input.Id ?? "", "rejected", problem));
                continue;
            }

            var id = input.Id!;
            if (!existing.Add(id))
            {
                duplicates++;
                results.Add(new IntegrationEventResult(id, "duplicate"));
                continue;
            }

            var credentialType = IntegrationContract.ParseCredentialType(input.Credential?.Type);
            var value = input.Credential?.Value is { Length: > 0 } raw ? EmployeeIdentifier.Normalize(raw) : null;
            var site = ResolveSite(sites, input.AccessPointId);
            var employeeId = await ResolveEmployeeAsync(input.PersonId, credentialType, value, ct);

            db.IntegrationEvents.Add(new IntegrationEvent
            {
                Source = source,
                ExternalId = id,
                Type = input.Type,
                OccurredAt = input.OccurredAt,
                ReceivedAt = now,
                AccessPointId = input.AccessPointId,
                SiteId = site?.Id,
                EmployeeId = employeeId,
                CredentialType = credentialType,
                CredentialValue = value,
                Direction = input.Direction ?? (input.Type == "vehicleIn" ? "in" : input.Type == "vehicleOut" ? "out" : null),
                Decision = input.Type == "denied" ? "deny" : input.Type is "passed" or "vehicleIn" or "vehicleOut" ? "allow" : null,
                DecisionReason = input.DecisionReason,
                Confidence = input.Confidence,
                Details = input.Details is { Count: > 0 } ? JsonSerializer.Serialize(input.Details) : null,
            });
            accepted++;
            results.Add(new IntegrationEventResult(id, "accepted"));
        }

        if (accepted > 0)
            await db.SaveChangesAsync(ct);

        return new IntegrationEventBatchResult(accepted, rejected, duplicates, results);
    }

    private static string? Validate(IntegrationEventInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Id))
            return "Chybí id události.";
        if (input.Id.Length > 128)
            return "Id události je delší než 128 znaků.";
        if (string.IsNullOrWhiteSpace(input.Type) || !KnownTypes.Contains(input.Type))
            return $"Neznámý druh události „{input.Type}“ (povolené: {string.Join(", ", KnownTypes)}).";
        if (input.OccurredAt == default)
            return "Chybí čas události (occurredAt).";
        if (input.Type == "denied" && string.IsNullOrWhiteSpace(input.DecisionReason))
            return "Zamítnutí musí mít důvod (decisionReason).";
        return null;
    }

    private static Site? ResolveSite(List<Site> sites, string? accessPointId)
    {
        if (string.IsNullOrWhiteSpace(accessPointId))
            return null;
        var id = accessPointId.Trim();
        return sites.FirstOrDefault(s => s.GateExternalIdList().Any(g => g.Equals(id, StringComparison.OrdinalIgnoreCase)))
               ?? sites.FirstOrDefault(s => s.IntegrationId().Equals(id, StringComparison.OrdinalIgnoreCase))
               ?? sites.FirstOrDefault(s => s.Code is not null && s.Code.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<int?> ResolveEmployeeAsync(string? personId, IdentifierType? credentialType, string? value,
        CancellationToken ct)
    {
        if (int.TryParse(personId, out var id) && await db.Employees.AnyAsync(e => e.Id == id, ct))
            return id;

        if (credentialType is null || value is null)
            return null;

        var matches = await db.EmployeeIdentifiers.AsNoTracking()
            .Where(i => i.Type == credentialType && i.Value == value)
            .Select(i => i.EmployeeId)
            .Distinct()
            .ToListAsync(ct);
        return matches.Count == 1 ? matches[0] : null;
    }
}
