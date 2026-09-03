using Acs.Infrastructure.Import;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Acs.Web.Pages.Admin;

/// <summary>Import čteček z tabulky „čtečky EKV“ dokumentace skutečného provedení.</summary>
public class ImportReadersModel(EkvReaderImportService importer) : PageModel
{
    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(
        string buildingName, IFormFile? file, string? dryRun, string? keepUnmatched)
    {
        if (file is null || file.Length == 0)
        {
            ErrorMessage = "Vyberte tabulku .xlsx.";
            return RedirectToPage();
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var rows = EkvReaderImportService.Parse(stream);
            if (rows.Count == 0)
            {
                ErrorMessage = "Tabulka neobsahuje žádný řádek s číslem čtečky.";
                return RedirectToPage();
            }

            var result = await importer.ImportAsync(
                rows, buildingName.Trim(),
                dryRun: dryRun == "true",
                deactivateUnmatched: keepUnmatched != "true",
                userName: User.Identity?.Name);

            Message = result.ToString();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Import selhal: {ex.Message}";
        }

        return RedirectToPage();
    }
}
