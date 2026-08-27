using Acs.Infrastructure.Import;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Acs.Web.Pages.Admin;

public class ImportPlanModel(PlanImportService importer) : PageModel
{
    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(
        string buildingName, IFormFile? file, string? dryRun, string? preferNonCorridor)
    {
        if (file is null || file.Length == 0)
        {
            ErrorMessage = "Vyberte soubor rooms.json.";
            return RedirectToPage();
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var plan = PlanImportService.Parse(stream);
            if (plan.Count == 0)
            {
                ErrorMessage = "Soubor neobsahuje žádné výkresy.";
                return RedirectToPage();
            }

            var result = await importer.ImportAsync(
                plan, buildingName.Trim(),
                dryRun: dryRun == "true",
                preferNonCorridor: preferNonCorridor == "true",
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
