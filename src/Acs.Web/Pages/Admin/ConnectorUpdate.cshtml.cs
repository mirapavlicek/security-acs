using Acs.Infrastructure.WinPak;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Acs.Web.Pages.Admin;

/// <summary>
/// Aktualizace WIN-PAK konektoru z ACS. Server WIN-PAKu nemá internet; ACS release
/// stáhne z GitHubu (nebo vezme balík od správce), ověří a pošle konektoru, který
/// vymění soubory sám. Stránka ukazuje běžící verzi, dostupná vydání a protokol.
/// </summary>
[RequestSizeLimit(400_000_000)]
public class ConnectorUpdateModel(WinPakClient winPak, ConnectorReleaseService releases) : PageModel
{
    public WinPakInfo? Info { get; private set; }
    public WinPakUpdateStatus? Status { get; private set; }
    public string? ConnectorError { get; private set; }
    public IReadOnlyList<ConnectorRelease> Releases { get; private set; } = [];
    public string? ReleasesError { get; private set; }

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public bool IsCurrent(ConnectorRelease release)
        => Info is { } info && Version.TryParse(info.Version, out var running) && Version.TryParse(release.Version, out var candidate)
           && new Version(running.Major, running.Minor, Math.Max(running.Build, 0)) == new Version(candidate.Major, candidate.Minor, Math.Max(candidate.Build, 0));

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            Info = await winPak.GetInfoAsync(ct);
            Status = await winPak.GetUpdateStatusAsync(ct);
        }
        catch (Exception ex)
        {
            ConnectorError = ex.Message;
        }

        try
        {
            Releases = await releases.ListReleasesAsync(ct: ct);
        }
        catch (Exception ex)
        {
            ReleasesError = ex.Message;
        }
    }

    public async Task<IActionResult> OnPostPushAsync(string version, bool startNow, CancellationToken ct)
    {
        try
        {
            var status = await releases.PushReleaseAsync(User.Identity?.Name, version, startNow, ct);
            Message = startNow
                ? $"Balík {version} poslán, konektor aktualizaci spustil — služba se za okamžik zastaví a znovu spustí. Za minutu stránku obnovte."
                : $"Balík {version} poslán a v konektoru ověřen (verze v balíku {status?.Staged?.Version}). Aktualizaci spusťte tlačítkem.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUploadAsync(IFormFile? package, bool startNow, CancellationToken ct)
    {
        if (package is null || package.Length == 0)
        {
            ErrorMessage = "Vyberte soubor balíku.";
            return RedirectToPage();
        }

        try
        {
            await using var stream = package.OpenReadStream();
            var status = await releases.PushPackageAsync(User.Identity?.Name, stream, startNow, ct);
            Message = $"Balík {status?.Staged?.Version} poslán konektoru{(startNow ? ", aktualizace spuštěna — za minutu stránku obnovte" : "")}.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostStartAsync(CancellationToken ct)
    {
        try
        {
            await winPak.StartUpdateAsync(ct);
            Message = "Aktualizace spuštěna — služba konektoru se za okamžik zastaví a znovu spustí. Za minutu stránku obnovte.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }
}
