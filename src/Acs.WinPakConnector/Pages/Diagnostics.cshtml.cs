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
            if (status.Busy is { Length: > 0 })
                return $"WIN-PAK je zaneprázdněn: {status.Busy}";
            var abandoned = provider is ComWinPakProvider { AbandonedCalls: > 0 } com
                ? $"; od startu opuštěno {com.AbandonedCalls} volání, která WIN-PAK nedokončil v limitu"
                : "";
            return $"databázový server {(status.DatabaseServerConnected ? "připojen" : "nepřipojen")}, "
                   + $"serverů se stavem: {status.Servers.Count}{abandoned}";
        });

        await RunAsync("Účty", async () =>
        {
            var accounts = await provider.GetAccountsAsync(ct);
            var list = accounts.Count == 0
                ? "žádný účet"
                : string.Join(", ", accounts.Select(a => $"{a.Name} ({a.SubAccounts.Count} podúčtů)"));

            // Čtečky, držitelé i karty jsou po účtech; s jakým účtem se pracuje, musí být vidět.
            var effective = provider.AccountName;
            var origin = provider is ComWinPakProvider { AccountResolvedAutomatically: true }
                ? " (doplněn automaticky — jediný ve WIN-PAKu; v konfiguraci není vyplněný)"
                : "";
            var subAccount = "";
            if (provider is ComWinPakProvider com && com.EffectiveSubAccountName is { Length: > 0 } sub)
            {
                subAccount = $", podúčet: {sub}"
                             + (com.SubAccountResolvedAutomatically ? " (doplněn automaticky — jediný u účtu)" : "");
            }

            return effective is null
                ? $"{list} — pracovní účet není nastaven"
                : $"{list} — pracovní účet: {effective}{origin}{subAccount}";
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
                var summary = $"zdroj {info.DataSourceName ?? "?"}, zóna {info.ServerTimeZone ?? "?"}, "
                              + $"operátor {info.CurrentOperator?.Name ?? "?"}, max. délka karty {info.MaxCardNumberLength}";
                if (info.Problems is { Count: > 0 } problems)
                    throw new InvalidOperationException($"{summary}; WIN-PAK odmítl: {string.Join("; ", problems)}");
                return summary;
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
