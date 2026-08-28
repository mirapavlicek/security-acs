using System.Runtime.Versioning;
using Acs.WinPakConnector.Configuration;
using Acs.WinPakConnector.Providers.Com;
using Microsoft.Extensions.Options;

namespace Acs.WinPakConnector.Providers;

/// <summary>
/// Drží aktuální provider a přestaví ho, jakmile se změní nastavení.
/// Díky tomu jde režim i přihlašovací údaje měnit z GUI bez restartu služby.
/// </summary>
public sealed class WinPakProviderCache(ConnectorSettingsStore store, ILogger<WinPakProviderCache> logger) : IDisposable
{
    private readonly Lock _gate = new();

    private IWinPakProvider? _provider;
    private string? _fingerprint;

    /// <summary>Provider odpovídající aktuálnímu nastavení.</summary>
    public IWinPakProvider Current
    {
        get
        {
            var fingerprint = store.Fingerprint();
            lock (_gate)
            {
                if (_provider is not null && _fingerprint == fingerprint)
                    return _provider;

                var settings = store.Current();
                var replacement = Create(settings);

                (_provider as IDisposable)?.Dispose();
                _provider = replacement;
                _fingerprint = fingerprint;

                logger.LogInformation("Provider WIN-PAK nastaven na režim {Mode}.", settings.Mode);
                return replacement;
            }
        }
    }

    /// <summary>Zahodí provider, takže se při dalším požadavku vytvoří znovu (např. po změně hesla operátora).</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            (_provider as IDisposable)?.Dispose();
            _provider = null;
            _fingerprint = null;
        }
    }

    private static IWinPakProvider Create(ConnectorSettings settings)
    {
        switch (settings.Mode.ToLowerInvariant())
        {
            case "mock":
                return new MockWinPakProvider();

            case "mssql":
                return new MssqlWinPakProvider(Options.Create(settings.Mssql));

            case "com":
                // COM je Windows-only; jinde konektor nemá jak WIN-PAK oslovit.
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException(
                        "Režim Com vyžaduje Windows — WIN-PAK API je vystavené přes COM+/DCOM. " +
                        "Na jiné platformě použijte režim Mock.");
                }

                return CreateCom(settings);

            default:
                throw new InvalidOperationException(
                    $"Neznámý režim '{settings.Mode}'. Povolené hodnoty: {string.Join(", ", ProviderModes.All)}.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static IWinPakProvider CreateCom(ConnectorSettings settings)
        => new ComWinPakProvider(Options.Create(settings.Com), new ComFactory());

    public void Dispose() => Invalidate();
}
