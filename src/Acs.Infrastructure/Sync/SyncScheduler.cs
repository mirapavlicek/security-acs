using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Acs.Infrastructure.Sync;

/// <summary>
/// Plánovač automatických synchronizací. Běží na obou HA nodech;
/// na MariaDB se pomocí GET_LOCK zajistí, že synchronizaci v daný okamžik
/// provádí jen jeden node (leader election přes DB → aplikace zůstává bezestavová).
/// </summary>
public class SyncScheduler(IServiceScopeFactory scopeFactory, ILogger<SyncScheduler> logger) : BackgroundService
{
    private const string ReadersLastRunKey = "Sync:ReadersLastRunUtc";
    private const string EmployeesLastRunKey = "Sync:EmployeesLastRunUtc";
    private const string AccessLastRunKey = "Sync:AccessLastRunUtc";
    private const string CardsLastRunKey = "Sync:CardsLastRunUtc";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunDueSyncsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Automatická synchronizace selhala.");
            }
        }
    }

    private async Task RunDueSyncsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var db = scope.ServiceProvider.GetRequiredService<AcsDbContext>();

        var readersDue = await IsDueAsync(settings, SettingKeys.WinPakSyncEnabled,
            SettingKeys.WinPakSyncIntervalMinutes, ReadersLastRunKey, ct);
        var employeesDue = await IsDueAsync(settings, SettingKeys.EmployeeSyncEnabled,
            SettingKeys.EmployeeSyncIntervalMinutes, EmployeesLastRunKey, ct);
        var accessDue = await IsDueAsync(settings, SettingKeys.WinPakAccessSyncEnabled,
            SettingKeys.WinPakAccessSyncIntervalMinutes, AccessLastRunKey, ct);
        var cardsDue = await IsDueAsync(settings, SettingKeys.CardsSyncEnabled,
            SettingKeys.CardsSyncIntervalMinutes, CardsLastRunKey, ct);

        if (!readersDue && !employeesDue && !accessDue && !cardsDue)
            return;

        await using var dbLock = await TryAcquireLockAsync(db, ct);
        if (dbLock is null)
        {
            logger.LogDebug("Synchronizaci právě provádí druhý node — přeskakuji.");
            return;
        }

        if (readersDue)
        {
            var sync = scope.ServiceProvider.GetRequiredService<ReaderSyncService>();
            var result = await sync.SyncAsync("system", ct);
            await settings.SetAsync(ReadersLastRunKey, DateTime.UtcNow.ToString("O"), "system", ct);
            logger.LogInformation("Synchronizace čteček: {Result}", result);
        }

        if (employeesDue)
        {
            var sync = scope.ServiceProvider.GetRequiredService<EmployeeSyncService>();
            var result = await sync.SyncAsync("system", ct);
            await settings.SetAsync(EmployeesLastRunKey, DateTime.UtcNow.ToString("O"), "system", ct);
            logger.LogInformation("Synchronizace zaměstnanců: {Result}", result);

            // Po importu zaměstnanců rovnou automatické zařazení dle oddělení.
            var autoAssign = scope.ServiceProvider.GetRequiredService<AutoAssignmentService>();
            var assignResult = await autoAssign.RunAsync("system", ct);
            if (assignResult.Created > 0)
                logger.LogInformation("Automatické zařazení: {Result}", assignResult);
        }

        if (cardsDue)
        {
            var sync = scope.ServiceProvider.GetRequiredService<CardSyncService>();
            var result = await sync.SyncAsync("system", ct);
            await settings.SetAsync(CardsLastRunKey, DateTime.UtcNow.ToString("O"), "system", ct);
            logger.LogInformation("Synchronizace karet: {Result}", result);
        }

        if (accessDue)
        {
            var sync = scope.ServiceProvider.GetRequiredService<AccessSyncService>();
            var result = await sync.SyncAsync("system", ct);
            await settings.SetAsync(AccessLastRunKey, DateTime.UtcNow.ToString("O"), "system", ct);
            logger.LogInformation("Zpětná synchronizace stavu z WIN-PAK: {Result}", result);
        }
    }

    private static async Task<bool> IsDueAsync(SettingsService settings,
        string enabledKey, string intervalKey, string lastRunKey, CancellationToken ct)
    {
        if (!await settings.GetBoolAsync(enabledKey, false, ct))
            return false;

        var interval = TimeSpan.FromMinutes(Math.Max(5, await settings.GetIntAsync(intervalKey, 60, ct)));
        var lastRun = DateTime.TryParse(await settings.GetAsync(lastRunKey, ct), null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTime.MinValue;
        return DateTime.UtcNow - lastRun >= interval;
    }

    /// <summary>Na MariaDB vrátí drženou GET_LOCK konexi (null = zámek drží jiný node); na SQLite vždy „zámek“.</summary>
    private static async Task<IAsyncDisposable?> TryAcquireLockAsync(AcsDbContext db, CancellationToken ct)
    {
        if (db.Database.ProviderName?.Contains("MySql", StringComparison.OrdinalIgnoreCase) != true)
            return new NoopLock();

        var connection = new MySqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct);
        await using (var cmd = new MySqlCommand("SELECT GET_LOCK('acs_sync', 0)", connection))
        {
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1)
                return new MySqlLock(connection);
        }

        await connection.DisposeAsync();
        return null;
    }

    private sealed class NoopLock : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MySqlLock(MySqlConnection connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var cmd = new MySqlCommand("SELECT RELEASE_LOCK('acs_sync')", connection);
                await cmd.ExecuteScalarAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
