using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Acs.Infrastructure.Sync;

/// <summary>Stav dlouhoběžící synchronizace, kterou uživatel spustil z GUI.</summary>
public record SyncJobStatus(
    string Name, bool Running, DateTime StartedAt, DateTime? FinishedAt,
    string? Result, string? Error)
{
    public TimeSpan Duration => (FinishedAt ?? DateTime.UtcNow) - StartedAt;
}

/// <summary>
/// Spouští synchronizace na pozadí. Import z AD u velkých domén trvá minuty —
/// v HTTP požadavku by vypršel timeout prohlížeče i proxy, proto se úloha
/// spustí na pozadí a stránka jen ukazuje její stav.
/// Běží vždy jen jedna úloha daného jména (dvojklik nic nerozbije).
/// </summary>
public class SyncJobRunner(IServiceScopeFactory scopeFactory, ILogger<SyncJobRunner> logger)
{
    private readonly ConcurrentDictionary<string, SyncJobStatus> _jobs = new();

    public IReadOnlyCollection<SyncJobStatus> Jobs => _jobs.Values.ToList();

    public SyncJobStatus? Get(string name) => _jobs.GetValueOrDefault(name);

    public bool IsRunning(string name) => _jobs.GetValueOrDefault(name)?.Running == true;

    /// <summary>
    /// Spustí úlohu na pozadí. Vrací false, pokud už stejná úloha běží.
    /// <para>
    /// Delegát dostane vlastní DI scope (vlastní DbContext) — nesmí sdílet ten z požadavku.
    /// <b>Nesahejte v něm na <c>HttpContext</c> ani <c>User</c></b>: úloha běží až po odeslání
    /// odpovědi, kdy je kontext uvolněný. Hodnoty z požadavku (např. jméno uživatele)
    /// si předejte do proměnné před voláním této metody.
    /// </para>
    /// </summary>
    public bool Start(string name, Func<IServiceProvider, CancellationToken, Task<string>> work)
    {
        var status = new SyncJobStatus(name, true, DateTime.UtcNow, null, null, null);
        if (!_jobs.TryAdd(name, status))
        {
            if (_jobs[name].Running)
                return false;
            _jobs[name] = status;
        }

        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            try
            {
                // Vlastní časový strop, ať úloha neběží donekonečna.
                using var cts = new CancellationTokenSource(TimeSpan.FromHours(2));
                var result = await work(scope.ServiceProvider, cts.Token);
                _jobs[name] = status with
                {
                    Running = false, FinishedAt = DateTime.UtcNow, Result = result,
                };
                logger.LogInformation("Úloha {Job} dokončena: {Result}", name, result);
            }
            catch (Exception ex)
            {
                _jobs[name] = status with
                {
                    Running = false, FinishedAt = DateTime.UtcNow, Error = ex.Message,
                };
                logger.LogError(ex, "Úloha {Job} selhala.", name);
            }
        });

        return true;
    }
}
