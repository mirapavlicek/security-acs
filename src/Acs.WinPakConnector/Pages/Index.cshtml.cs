using Acs.WinPakConnector.Auth;
using Acs.WinPakConnector.Configuration;
using Acs.WinPakConnector.Models;
using Acs.WinPakConnector.Providers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Acs.WinPakConnector.Pages;

public class IndexModel(ConnectorSettingsStore store, WinPakProviderCache providers) : PageModel
{
    public ConnectorSettings Settings { get; private set; } = new();
    public ConnectorStatusDto? Status { get; private set; }
    public string? StatusError { get; private set; }
    public bool SupportsWrite { get; private set; }
    public bool SupportsDoorControl { get; private set; }

    public string Version => typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev";
    public string SettingsFile => store.FilePath;

    /// <summary>Věci, které je potřeba dořešit, aby konektor fungoval naplno.</summary>
    public List<string> Warnings { get; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Settings = store.Current();

        try
        {
            var provider = providers.Current;
            SupportsWrite = provider.SupportsWrite;
            SupportsDoorControl = provider.SupportsDoorControl;
            Status = await provider.GetStatusAsync(ct);
        }
        catch (Exception ex)
        {
            StatusError = ex.Message;
        }

        CollectWarnings();
    }

    private void CollectWarnings()
    {
        if (string.IsNullOrWhiteSpace(Settings.ApiKey))
            Warnings.Add("Není nastavený API klíč — konektor odmítá všechny požadavky z ACS.");

        if (string.IsNullOrWhiteSpace(Settings.AdminPassword))
            Warnings.Add("Administrace se přihlašuje API klíčem. Nastavte si samostatné heslo, ať klíč nemusíte zadávat do prohlížeče.");

        if (Settings.Mode.Equals(ProviderModes.Mock, StringComparison.OrdinalIgnoreCase))
            Warnings.Add("Běží režim Mock s ukázkovými daty — pro ostrý provoz přepněte na Com.");

        if (Settings.Mode.Equals(ProviderModes.Mssql, StringComparison.OrdinalIgnoreCase))
            Warnings.Add("Režim Mssql je jen pro čtení; přístupy musí správce karet zadávat ve WIN-PAK ručně.");

        if (Settings.Mode.Equals(ProviderModes.Com, StringComparison.OrdinalIgnoreCase)
            && !Settings.Com.EnableCommunicationServer)
        {
            Warnings.Add("Komunikační server je vypnutý — nefunguje ovládání dveří ani odběr událostí z panelů.");
        }

        if (Status is { Error.Length: > 0 })
            Warnings.Add($"Konektor se nepřipojil k WIN-PAK: {Status.Error}");

        foreach (var server in Status?.Servers.Where(s => !s.Connected) ?? [])
            Warnings.Add($"Server WIN-PAK {server.ServerName} hlásí, že není připojený.");
    }

    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await HttpContext.SignOutAsync(AdminAuthentication.Scheme);
        return RedirectToPage("/Login");
    }
}
