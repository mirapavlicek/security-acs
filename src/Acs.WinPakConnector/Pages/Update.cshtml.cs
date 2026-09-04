using Acs.WinPakConnector.Update;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Acs.WinPakConnector.Pages;

/// <summary>
/// Aktualizace konektoru z prohlížeče: správce nahraje balík releasu, konektor ho
/// ověří a vymění soubory. Pro server bez internetu — balík se stáhne jinde a sem
/// se jen pošle. Stejnou cestu (API) používá ACS.
/// </summary>
[RequestSizeLimit(400_000_000)]
public class UpdateModel(ConnectorUpdater updater) : PageModel
{
    public UpdateStatusDto Status { get; private set; } = null!;

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public void OnGet() => Status = updater.Status();

    public async Task<IActionResult> OnPostUploadAsync(IFormFile? package, string? sha256, bool startNow, CancellationToken ct)
    {
        if (package is null || package.Length == 0)
        {
            ErrorMessage = "Vyberte soubor balíku (AcsWinPakConnector-<verze>-win-x64.zip).";
            return RedirectToPage();
        }

        try
        {
            await using var stream = package.OpenReadStream();
            var staged = await updater.StageAsync(stream, sha256, ct);
            if (startNow)
            {
                updater.Start();
                Message = $"Balík {staged.Version} přijat, aktualizace spuštěna — služba se za okamžik zastaví a znovu spustí. Za minutu stránku obnovte.";
            }
            else
            {
                Message = $"Balík {staged.Version} přijat a ověřen (SHA-256 {staged.Sha256[..12]}…). Aktualizaci spusťte tlačítkem.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }

    public IActionResult OnPostStart()
    {
        try
        {
            var staged = updater.Start();
            Message = $"Aktualizace na {staged.Version} spuštěna — služba se za okamžik zastaví a znovu spustí. Za minutu stránku obnovte.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }
}
