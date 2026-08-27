using System.Net.Sockets;
using Acs.Infrastructure.Settings;
using DnsClient;
using Microsoft.Extensions.Logging;

namespace Acs.Infrastructure.Auth;

/// <summary>Kandidát na doménový řadič ze SRV záznamu.</summary>
public record DcCandidate(string Host, int Priority, int Weight);

/// <summary>
/// DC lokátor — stejný princip jako Windows DC Locator: dotaz na DNS SRV záznamy
/// <c>_ldap._tcp.dc._msdcs.&lt;doména&gt;</c>, seřazení podle priority a váhy,
/// ověření dostupnosti (TCP na LDAP/LDAPS port) a vrácení aktivního řadiče.
/// Výsledek se cachuje; při výpadku volající zavolá <see cref="Invalidate"/>
/// a lokátor najde další živý DC. Ručně nastavený server (Nastavení → AD)
/// slouží jako fallback, když SRV dotaz nic nevrátí.
/// </summary>
public class DcLocator(
    SettingsService settings,
    ILogger<DcLocator> logger,
    Func<string, CancellationToken, Task<IReadOnlyList<DcCandidate>>>? srvQuery = null,
    Func<string, int, CancellationToken, Task<bool>>? probe = null)
{
    private static readonly LookupClient Dns = new(new LookupClientOptions
    {
        Timeout = TimeSpan.FromSeconds(3),
        UseCache = true,
    });

    private static readonly object CacheLock = new();
    private static string? _cachedServer;
    private static DateTime _cacheUntil = DateTime.MinValue;

    private readonly Func<string, CancellationToken, Task<IReadOnlyList<DcCandidate>>> _srvQuery
        = srvQuery ?? QuerySrvAsync;
    private readonly Func<string, int, CancellationToken, Task<bool>> _probe
        = probe ?? ProbeTcpAsync;

    /// <summary>Vrátí aktivní LDAP server (hostname). Vyhodí výjimku, když žádný není dostupný.</summary>
    public async Task<string> GetActiveServerAsync(CancellationToken ct = default)
    {
        lock (CacheLock)
        {
            if (_cachedServer is not null && DateTime.UtcNow < _cacheUntil)
                return _cachedServer;
        }

        var port = await settings.GetBoolAsync(SettingKeys.LdapUseSsl, true, ct)
            ? await settings.GetIntAsync(SettingKeys.LdapPort, 636, ct)
            : await settings.GetIntAsync(SettingKeys.LdapPort, 389, ct);

        foreach (var host in await GetCandidateServersAsync(ct))
        {
            if (await _probe(host, port, ct))
            {
                lock (CacheLock)
                {
                    _cachedServer = host;
                    _cacheUntil = DateTime.UtcNow.AddMinutes(5);
                }

                logger.LogDebug("DC lokátor: aktivní řadič {Host}:{Port}.", host, port);
                return host;
            }

            logger.LogWarning("DC lokátor: řadič {Host}:{Port} neodpovídá, zkouším další.", host, port);
        }

        throw new InvalidOperationException(
            "DC lokátor nenašel žádný dostupný doménový řadič (SRV záznamy ani nastavený server neodpovídají).");
    }

    /// <summary>Zneplatní cache — po chybě spojení se najde jiný živý řadič.</summary>
    public static void Invalidate()
    {
        lock (CacheLock)
        {
            _cachedServer = null;
            _cacheUntil = DateTime.MinValue;
        }
    }

    /// <summary>Kandidáti v pořadí: SRV záznamy (priorita/váha), pak ručně nastavený server.</summary>
    public async Task<IReadOnlyList<string>> GetCandidateServersAsync(CancellationToken ct = default)
    {
        var result = new List<string>();

        if (await settings.GetBoolAsync(SettingKeys.LdapUseDcLocator, true, ct))
        {
            var domain = await settings.GetAsync(SettingKeys.LdapDomain, ct);
            if (!string.IsNullOrWhiteSpace(domain))
            {
                try
                {
                    var candidates = await _srvQuery(domain.Trim(), ct);
                    result.AddRange(OrderCandidates(candidates));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "DC lokátor: SRV dotaz pro doménu {Domain} selhal.", domain);
                }
            }
        }

        var configured = await settings.GetAsync(SettingKeys.LdapServer, ct);
        if (!string.IsNullOrWhiteSpace(configured)
            && !result.Contains(configured.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            result.Add(configured.Trim());
        }

        return result;
    }

    /// <summary>SRV pořadí: nižší priorita dřív; v rámci priority vyšší váha dřív.</summary>
    public static List<string> OrderCandidates(IReadOnlyList<DcCandidate> candidates)
        => candidates
            .OrderBy(c => c.Priority)
            .ThenByDescending(c => c.Weight)
            .Select(c => c.Host.TrimEnd('.'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static async Task<IReadOnlyList<DcCandidate>> QuerySrvAsync(string domain, CancellationToken ct)
    {
        var response = await Dns.QueryAsync($"_ldap._tcp.dc._msdcs.{domain}", QueryType.SRV, cancellationToken: ct);
        return response.Answers.SrvRecords()
            .Select(r => new DcCandidate(r.Target.Value, r.Priority, r.Weight))
            .ToList();
    }

    private static async Task<bool> ProbeTcpAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
