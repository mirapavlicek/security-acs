using Acs.WinPakConnector.Models;
using Acs.WinPakConnector.Providers;
using Acs.WinPakConnector.Providers.Com;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Acs.WinPakConnector.Pages;

/// <summary>
/// Živé ověření, že konektor opravdu čte z WIN-PAKu. Každý test se provádí
/// samostatně, aby jedna nefunkční oblast neskryla výsledek ostatních.
/// </summary>
public class DiagnosticsModel(WinPakProviderCache providers) : PageModel
{
    public record Probe(string Name, bool Succeeded, string Result);

    public List<Probe> Probes { get; } = [];
    public IReadOnlyList<WinPakEvent> Events { get; private set; } = [];
    public string? EventsError { get; private set; }
    public string Mode { get; private set; } = "";

    public async Task OnGetAsync(CancellationToken ct)
    {
        IWinPakProvider provider;
        try
        {
            provider = providers.Current;
            Mode = provider.Mode;
        }
        catch (Exception ex)
        {
            Probes.Add(new Probe("Vytvoření providera", false, ex.Message));
            return;
        }

        await RunAsync("Stav spojení", async () =>
        {
            var status = await provider.GetStatusAsync(ct);
            if (status.Error is { Length: > 0 })
                throw new InvalidOperationException(status.Error);
            return $"databázový server {(status.DatabaseServerConnected ? "připojen" : "nepřipojen")}, "
                   + $"serverů se stavem: {status.Servers.Count}";
        });

        await RunAsync("Účty", async () =>
        {
            var accounts = await provider.GetAccountsAsync(ct);
            return accounts.Count == 0
                ? "žádný účet"
                : string.Join(", ", accounts.Select(a => $"{a.Name} ({a.SubAccounts.Count} podúčtů)"));
        });

        await RunAsync("Čtečky", async () => $"{(await provider.GetReadersAsync(ct)).Count} čteček");

        await RunAsync("Přístupové úrovně", async () => $"{(await provider.GetAccessLevelsAsync(ct)).Count} úrovní");

        await RunAsync("Držitelé karet", async () =>
            $"{(await provider.SearchCardHoldersAsync(null, ct)).Count} držitelů");

        if (provider is IWinPakCatalogApi catalog)
        {
            await RunAsync("Systémové údaje", async () =>
            {
                var info = await catalog.GetSystemInfoAsync(ct);
                return $"zdroj {info.DataSourceName ?? "?"}, zóna {info.ServerTimeZone ?? "?"}, "
                       + $"operátor {info.CurrentOperator?.Name ?? "?"}, max. délka karty {info.MaxCardNumberLength}";
            });

            await RunAsync("Časové zóny", async () => $"{(await catalog.GetTimeZonesAsync(ct)).Count} zón");
            await RunAsync("Panely", async () => $"{(await catalog.GetPanelsAsync(ct)).Count} panelů");
        }

        if (provider.SupportsDoorControl)
            await RunAsync("Připojená zařízení", async () => $"{(await provider.GetDevicesAsync(ct)).Count} zařízení");

        await LoadEventsAsync(provider, ct);
    }

    private async Task RunAsync(string name, Func<Task<string>> probe)
    {
        try
        {
            Probes.Add(new Probe(name, true, await probe()));
        }
        catch (NotSupportedException ex)
        {
            Probes.Add(new Probe(name, false, $"nepodporováno v tomto režimu — {ex.Message}"));
        }
        catch (Exception ex)
        {
            Probes.Add(new Probe(name, false, ex.Message));
        }
    }

    private async Task LoadEventsAsync(IWinPakProvider provider, CancellationToken ct)
    {
        if (provider is not ComWinPakProvider com)
        {
            EventsError = $"Režim {provider.Mode} události z panelů neodebírá.";
            return;
        }

        try
        {
            Events = await com.GetRecentEventsAsync(20, ct);
        }
        catch (Exception ex)
        {
            EventsError = ex.Message;
        }
    }
}
