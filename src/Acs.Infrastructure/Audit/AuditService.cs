using Acs.Domain.Entities;
using Acs.Infrastructure.Data;

namespace Acs.Infrastructure.Audit;

/// <summary>Zápis auditních záznamů (přihlášení, změny číselníků, rozhodnutí…).</summary>
public class AuditService(AcsDbContext db)
{
    public async Task LogAsync(string? userName, string action, string? entity = null,
        string? entityId = null, string? details = null, CancellationToken ct = default)
    {
        db.AuditLogs.Add(new AuditLog
        {
            At = DateTime.UtcNow,
            UserName = userName,
            Action = action,
            Entity = entity,
            EntityId = entityId,
            Details = details,
        });
        await db.SaveChangesAsync(ct);
    }
}
