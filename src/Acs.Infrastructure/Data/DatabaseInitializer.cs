using Acs.Infrastructure.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Acs.Infrastructure.Data;

/// <summary>
/// Inicializace DB při startu aplikace. Na MariaDB drží po dobu migrace
/// zámek GET_LOCK, aby se při současném startu obou HA nodů nespustily
/// migrace dvakrát (HAProxy/failover směruje oba nody na tentýž uzel Galery).
/// </summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(AcsDbContext db, ILogger? logger = null,
        string? bootstrapAdminPassword = null, CancellationToken ct = default)
    {
        if (db.Database.ProviderName?.Contains("MySql", StringComparison.OrdinalIgnoreCase) == true)
        {
            var connectionString = db.Database.GetConnectionString()
                ?? throw new InvalidOperationException("Chybí connection string.");

            await using var lockConnection = new MySqlConnection(connectionString);
            await lockConnection.OpenAsync(ct);
            await using (var acquire = new MySqlCommand("SELECT GET_LOCK('acs_migrations', 120)", lockConnection))
            {
                var acquired = Convert.ToInt32(await acquire.ExecuteScalarAsync(ct)) == 1;
                if (!acquired)
                    throw new TimeoutException("Nepodařilo se získat zámek pro DB migrace (acs_migrations).");
            }

            try
            {
                await db.Database.MigrateAsync(ct);
            }
            finally
            {
                await using var release = new MySqlCommand("SELECT RELEASE_LOCK('acs_migrations')", lockConnection);
                await release.ExecuteScalarAsync(ct);
            }
        }
        else
        {
            // SQLite (vývoj/testy) — schéma přímo z modelu.
            await db.Database.EnsureCreatedAsync(ct);
        }

        await UserAuthenticationService.SeedLocalAdminAsync(db, logger, bootstrapAdminPassword, ct);
    }
}
