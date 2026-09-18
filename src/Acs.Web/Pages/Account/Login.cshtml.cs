using Acs.Infrastructure.Audit;
using Acs.Domain.Entities;
using Acs.Infrastructure.Auth;
using Acs.Infrastructure.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace Acs.Web.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting("login")]
public class LoginModel(UserAuthenticationService auth, AuditService audit, SettingsService settings, ILogger<LoginModel> logger) : PageModel
{
    [BindProperty]
    public string UserName { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    public string? ErrorMessage { get; set; }

    /// <summary>Zpráva bez chybového rázu (např. po odhlášení).</summary>
    public string? InfoMessage { get; set; }

    /// <summary>Je zapnuté přihlášení účtem Windows — zobrazí se tlačítko nad formulářem.</summary>
    public bool WindowsLoginEnabled { get; private set; }

    public string? ReturnUrl { get; private set; }

    /// <summary>Doména, která se k AD účtu doplní automaticky (nápověda ve formuláři).</summary>
    public string LdapDomain { get; private set; } = SettingsService.DefaultLdapDomain;

    /// <param name="manual">Zobrazit formulář i při zapnutém automatickém přihlášení Windows (odhlášení, jiný účet, lokální admin).</param>
    /// <param name="sso">Kód výsledku přihlášení Windows, ze kterého se sem uživatel vrátil.</param>
    public async Task<IActionResult> OnGetAsync(string? returnUrl = null, string? manual = null, string? sso = null)
    {
        ReturnUrl = returnUrl;
        var showForm = manual is "1" or "true";
        // Bez keytabu Kerberos ticket nikdo neověří (UnknownCredentials) — tlačítko ani automatická
        // výzva Negotiate se nenabízí, uživatel dostane rovnou formulář.
        WindowsLoginEnabled = await settings.GetBoolAsync(SettingKeys.SsoEnabled) && KerberosKeytab.IsAvailable;
        LdapDomain = await settings.GetLdapDomainAsync();
        var autoLogin = WindowsLoginEnabled && await settings.GetBoolAsync(SettingKeys.SsoAutoLogin);

        if (autoLogin && !showForm && sso is null)
            return RedirectToPage("/Account/WindowsLogin", new { returnUrl });

        ErrorMessage = sso switch
        {
            null => null,
            "failed" => "Přihlášení účtem Windows se nezdařilo (prohlížeč neposlal platný Kerberos/NTLM token). Přihlaste se jménem a heslem.",
            "account" => "Účet Windows byl ověřen, ale v ACS ho nelze použít (neaktivní uživatel nebo nepovolená doména). Přihlaste se jménem a heslem, nebo kontaktujte správce.",
            "disabled" => "Přihlášení účtem Windows není zapnuté. Přihlaste se jménem a heslem.",
            "nokeytab" => "Přihlášení účtem Windows na tomto serveru zatím není k dispozici (chybí Kerberos keytab). Přihlaste se jménem a heslem.",
            _ => "Přihlášení účtem Windows se nezdařilo. Přihlaste se jménem a heslem.",
        };
        if (sso is null && showForm && Request.Query.ContainsKey("loggedOut"))
            InfoMessage = "Byli jste odhlášeni.";

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        ReturnUrl = returnUrl;
        WindowsLoginEnabled = await settings.GetBoolAsync(SettingKeys.SsoEnabled);
        LdapDomain = await settings.GetLdapDomainAsync();

        if (string.IsNullOrWhiteSpace(UserName) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Zadejte uživatelské jméno a heslo.";
            return Page();
        }

        AppUser? user;
        try
        {
            user = await auth.AuthenticateAsync(UserName, Password);
        }
        catch (LdapUnavailableException ex)
        {
            logger.LogError(ex, "Přihlášení {User}: Active Directory neodpovídá.", UserName);
            await audit.LogAsync(UserName, "login-failed", details: "AD: " + ex.Message);
            ErrorMessage = $"Ověření účtem domény teď nelze provést: {ex.Message} Lokální účet se přihlásí i tak.";
            return Page();
        }
        catch (Exception ex)
        {
            // Přihlašovací stránka nikdy nesmí skončit chybovou stránkou — uživatel potřebuje vědět, že to není jeho heslo.
            logger.LogError(ex, "Přihlášení {User}: neočekávaná chyba při ověření.", UserName);
            await audit.LogAsync(UserName, "login-failed", details: $"chyba: {ex.GetType().Name}: {ex.Message}");
            ErrorMessage = "Přihlášení se nezdařilo kvůli chybě na serveru (zapsáno do logu). Zkuste to znovu nebo použijte lokální účet.";
            return Page();
        }

        if (user is null)
        {
            await audit.LogAsync(UserName, "login-failed");
            ErrorMessage = "Neplatné přihlašovací údaje.";
            return Page();
        }

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            AppUserPrincipal.Create(user, "password"));

        await audit.LogAsync(user.UserName, "login");

        if (user.MustChangePassword)
            return RedirectToPage("/Account/ChangePassword");

        return LocalRedirect(returnUrl is { Length: > 0 } && Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
    }

    public async Task<IActionResult> OnPostLogoutAsync()
    {
        var userName = User.Identity?.Name;
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (userName is not null)
            await audit.LogAsync(userName, "logout");
        // manual=1: po odhlášení se nesmí automaticky přihlásit znovu účtem Windows.
        return RedirectToPage("/Account/Login", new { manual = 1, loggedOut = 1 });
    }
}
