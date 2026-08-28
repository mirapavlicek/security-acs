using Acs.WinPakConnector.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Acs.WinPakConnector.Pages;

[AllowAnonymous]
public class LoginModel(AdminAuthentication auth) : PageModel
{
    [BindProperty] public string Password { get; set; } = "";

    public string? ErrorMessage { get; private set; }

    /// <summary>Bez API klíče i hesla se do administrace nedá dostat — je potřeba doplnit konfiguraci.</summary>
    public bool IsLocked => !auth.IsConfigured;

    public bool UsesSeparatePassword => auth.UsesSeparatePassword;

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        if (IsLocked)
        {
            ErrorMessage = "Konektor nemá nastavený API klíč ani heslo administrace.";
            return Page();
        }

        var principal = await auth.AuthenticateAsync(Password, HttpContext.RequestAborted);
        if (principal is null)
        {
            ErrorMessage = "Neplatné heslo.";
            return Page();
        }

        await HttpContext.SignInAsync(AdminAuthentication.Scheme, principal);
        return LocalRedirect(returnUrl is { Length: > 0 } && Url.IsLocalUrl(returnUrl) ? returnUrl : "/ui");
    }
}
