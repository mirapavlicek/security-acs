using Acs.WinPakConnector.Providers;
using Acs.WinPakConnector.Providers.Com;
using Acs.WinPakConnector.Providers.Com.Signatures;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Acs.WinPakConnector.Pages;

/// <summary>
/// Kontrola všech volání konektoru proti skutečným signaturám objektů WIN-PAKu.
/// Rozdíly mezi příručkou a skutečnou instalací se dosud objevovaly jeden po
/// druhém při používání; tady jsou vidět všechny najednou a bez volání do databáze.
/// </summary>
public class SignaturesModel(WinPakProviderCache providers) : PageModel
{
    public IReadOnlyList<SignatureCheckResult> Results { get; private set; } = [];
    public string? Error { get; private set; }
    public string Mode { get; private set; } = "";
    public bool Ran { get; private set; }

    public int Count(SignatureVerdict verdict) => Results.Count(r => r.Verdict == verdict);

    /// <summary>Textová podoba výsledku k zkopírování do hlášení.</summary>
    public string AsText => string.Join("\n", Results
        .Where(r => r.Verdict != SignatureVerdict.Ok)
        .Select(r => $"{Label(r.Verdict)} {r.Method}\n  konektor: {r.Sent}\n  WIN-PAK:  {r.Actual ?? "—"}\n  {r.Note}"));

    public static string Label(SignatureVerdict verdict) => verdict switch
    {
        SignatureVerdict.Ok => "sedí",
        SignatureVerdict.Learnable => "vyrovná se za běhu",
        SignatureVerdict.Mismatch => "rozdíl",
        SignatureVerdict.Missing => "metoda chybí",
        _ => "nelze ověřit",
    };

    public static string Badge(SignatureVerdict verdict) => verdict switch
    {
        SignatureVerdict.Ok => "badge-ok",
        SignatureVerdict.Learnable => "badge-primary",
        SignatureVerdict.Mismatch or SignatureVerdict.Missing => "badge-bad",
        _ => "",
    };

    public async Task OnGetAsync(bool run, CancellationToken ct)
    {
        try
        {
            var provider = providers.Current;
            Mode = provider.Mode;
            if (!run)
                return;

            Ran = true;
            if (provider is not ComWinPakProvider com)
            {
                Error = $"Režim {Mode} nemá COM objekty WIN-PAKu, které by šlo popsat. Přepněte v Nastavení na režim Com.";
                return;
            }

            Results = await com.CheckSignaturesAsync(ct);
        }
        catch (Exception ex)
        {
            Ran = true;
            Error = ex.Message;
        }
    }
}
