using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Acs.Web.Pages;

/// <summary>
/// Chybová stránka pro výjimky i pro stavové kódy bez těla (přes
/// <c>UseStatusCodePagesWithReExecute</c>). Řekne uživateli, co se stalo a co
/// s tím udělat, a správci dá identifikátor požadavku do logu.
/// </summary>
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public class ErrorModel : PageModel
{
    public string? RequestId { get; private set; }
    public int StatusCode { get; private set; }
    public string Title { get; private set; } = "";
    public string Explanation { get; private set; } = "";
    public string? OriginalPath { get; private set; }

    public void OnGet(int? statusCode) => Describe(statusCode);

    /// <summary>Znovu vykonaný požadavek si nese původní metodu — chybný POST sem přijde jako POST.</summary>
    public void OnPost(int? statusCode) => Describe(statusCode);

    private void Describe(int? statusCode)
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
        var reExecute = HttpContext.Features.Get<IStatusCodeReExecuteFeature>();
        OriginalPath = reExecute?.OriginalPath;
        StatusCode = reExecute?.OriginalStatusCode
                     ?? statusCode
                     ?? (HttpContext.Features.Get<IExceptionHandlerFeature>() is not null ? 500 : Response.StatusCode);

        (Title, Explanation) = StatusCode switch
        {
            400 => ("Požadavek server nepřijal",
                "Nejčastěji vypršela platnost formuláře (stránka byla dlouho otevřená, nebo se mezitím aplikace aktualizovala), "
                + "nebo byl požadavek příliš velký. Načtěte stránku znovu a akci zopakujte."),
            403 => ("Na tuhle akci nemáte oprávnění", "Pokud ji potřebujete, požádejte administrátora o příslušnou roli."),
            404 => ("Stránka nebyla nalezena", "Odkaz je neplatný, nebo byl záznam mezitím odstraněn."),
            413 => ("Požadavek je příliš velký", "Zmenšete výběr (méně označených položek nebo menší soubor) a zkuste to znovu."),
            429 => ("Příliš mnoho pokusů", "Chvíli počkejte a zkuste to znovu."),
            502 or 503 or 504 => ("Aplikace se právě restartuje nebo aktualizuje", "Za chvíli stránku obnovte."),
            _ => ("Něco se nepovedlo", "Chyba je zapsaná v logu aplikace i s identifikátorem níže. Zkuste akci zopakovat; pokud se opakuje, pošlete identifikátor správci."),
        };
    }
}
