using Acs.WinPakConnector.Providers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Acs.WinPakConnector.Pages.Features;

/// <summary>
/// Základ stránek sekce „Funkce“. Sjednocuje přístup k provideru a hlavně
/// překlad chyb: operace, kterou aktuální režim neumí, musí skončit srozumitelnou
/// hláškou, ne chybovou stránkou.
/// </summary>
public abstract class FeaturePageModel(WinPakProviderCache providers) : PageModel
{
    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    /// <summary>Chyba, která nastala při načítání stránky (na rozdíl od akcí se nepřesměrovává).</summary>
    public string? LoadError { get; private set; }

    public string Mode { get; private set; } = "";

    public bool SupportsWrite { get; private set; }

    public bool SupportsDoorControl { get; private set; }

    protected IWinPakProvider Provider => providers.Current;

    /// <summary>Rozšířená část API; null, pokud ji aktuální režim neumí.</summary>
    protected IWinPakCatalogApi? Catalog => providers.Current as IWinPakCatalogApi;

    protected IWinPakCatalogApi RequireCatalog()
        => Catalog ?? throw new NotSupportedException(
            $"Režim {Provider.Mode} tuto část WIN-PAK API nepodporuje. Přepněte v Nastavení na režim Com.");

    public override async Task OnPageHandlerExecutionAsync(
        PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        try
        {
            Mode = Provider.Mode;
            SupportsWrite = Provider.SupportsWrite;
            SupportsDoorControl = Provider.SupportsDoorControl;
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
        }

        await next();
    }

    /// <summary>Načtení dat pro zobrazení — chyba se ukáže v hlavičce stránky a zbytek se vykreslí dál.</summary>
    protected async Task LoadAsync(Func<Task> load)
    {
        if (LoadError is not null)
            return;

        try
        {
            await load();
        }
        catch (Exception ex)
        {
            LoadError = Describe(ex);
        }
    }

    /// <summary>Provede akci a vrátí se na stránku s hláškou o výsledku.</summary>
    protected async Task<IActionResult> ActAsync(string description, Func<Task> action)
    {
        try
        {
            await action();
            Message = $"{description} — hotovo.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{description} se nezdařilo: {Describe(ex)}";
        }

        return RedirectToPage();
    }

    /// <summary>Varianta pro akce, které mají vrátit hodnotu k zobrazení.</summary>
    protected async Task<IActionResult> ActAsync(string description, Func<Task<string>> action)
    {
        try
        {
            Message = $"{description}: {await action()}";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{description} se nezdařilo: {Describe(ex)}";
        }

        return RedirectToPage();
    }

    private static string Describe(Exception exception) => exception switch
    {
        NotSupportedException => exception.Message,
        KeyNotFoundException => exception.Message,
        _ => exception.Message,
    };
}
