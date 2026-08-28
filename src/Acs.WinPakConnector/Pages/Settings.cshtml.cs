using Acs.WinPakConnector.Configuration;
using Acs.WinPakConnector.Providers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Acs.WinPakConnector.Pages;

public class SettingsModel(ConnectorSettingsStore store, WinPakProviderCache providers) : PageModel
{
    /// <summary>Editovaná kopie nastavení. Tajné hodnoty se do formuláře nepředvyplňují.</summary>
    [BindProperty] public ConnectorSettings Input { get; set; } = new();

    /// <summary>Nově vygenerovaný klíč se ukáže jednou, aby si ho šlo zkopírovat do ACS.</summary>
    [BindProperty] public string? GeneratedApiKey { get; set; }

    public string CurrentApiKeyMask { get; private set; } = "";
    public bool HasAdminPassword { get; private set; }
    public bool HasComPassword { get; private set; }
    public bool HasConnectionString { get; private set; }
    public string SettingsFile => store.FilePath;

    [TempData] public string? Message { get; set; }

    public List<string> Errors { get; } = [];

    public void OnGet()
    {
        var current = store.Current();
        Input = current.Clone();
        ClearSecrets();
        Describe(current);
    }

    public IActionResult OnPostGenerateKey()
    {
        var current = store.Current();
        Describe(current);
        GeneratedApiKey = ConnectorSettingsStore.GenerateApiKey();
        Input.ApiKey = GeneratedApiKey;
        return Page();
    }

    public IActionResult OnPostSave()
    {
        var current = store.Current();

        // Prázdné tajné pole znamená „nechat beze změny“ — doplníme aktuální hodnotu,
        // aby validace posuzovala stav, který po uložení opravdu nastane.
        var candidate = Input.Clone();
        candidate.ApiKey = Fallback(candidate.ApiKey, current.ApiKey);
        candidate.AdminPassword = Fallback(candidate.AdminPassword, current.AdminPassword);
        candidate.Com.Password = Fallback(candidate.Com.Password, current.Com.Password);
        candidate.Mssql.ConnectionString = Fallback(candidate.Mssql.ConnectionString, current.Mssql.ConnectionString);

        Errors.AddRange(candidate.Validate().Select(v => v.ErrorMessage ?? "Neplatná hodnota."));
        if (Errors.Count > 0)
        {
            Describe(current);
            return Page();
        }

        store.Save(Input);
        providers.Invalidate();

        Message = "Nastavení uloženo a použito. Restart služby není potřeba.";
        return RedirectToPage();
    }

    /// <summary>Smaže samostatné heslo administrace — pak se zase přihlašuje API klíčem.</summary>
    public IActionResult OnPostClearAdminPassword()
    {
        var settings = store.Current();
        settings.AdminPassword = " ";   // prázdný řetězec by se bral jako „beze změny“
        store.Save(settings);

        Message = "Heslo administrace zrušeno, přihlašuje se znovu API klíčem.";
        return RedirectToPage();
    }

    private static string Fallback(string value, string current)
        => string.IsNullOrWhiteSpace(value) ? current : value;

    private void ClearSecrets()
    {
        Input.ApiKey = "";
        Input.AdminPassword = "";
        Input.Com.Password = "";
        Input.Mssql.ConnectionString = "";
    }

    private void Describe(ConnectorSettings current)
    {
        CurrentApiKeyMask = ConnectorSettingsStore.Mask(current.ApiKey);
        HasAdminPassword = !string.IsNullOrWhiteSpace(current.AdminPassword);
        HasComPassword = !string.IsNullOrWhiteSpace(current.Com.Password);
        HasConnectionString = !string.IsNullOrWhiteSpace(current.Mssql.ConnectionString);
    }
}
