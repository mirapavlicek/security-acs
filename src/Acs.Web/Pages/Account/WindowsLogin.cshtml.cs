using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Auth;
using Acs.Infrastructure.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Acs.Web.Pages.Account;

/// <summary>
/// Přihlášení účtem Windows (Negotiate: Kerberos, případně NTLM).
/// Prohlížeč na doménovém PC dostane výzvu <c>WWW-Authenticate: Negotiate</c>, pošle token,
/// Kestrel identitu ověří (na Linuxu přes GSSAPI + keytab) a stránka ji namapuje na uživatele ACS
/// a vydá běžnou přihlašovací cookie. Při jakémkoli selhání se uživatel vrátí na formulář
/// se jménem a heslem (<c>/Account/Login?manual=1&amp;sso=…</c>), nikdy nezůstane „zaseknutý“.
/// </summary>
[AllowAnonymous]
public class WindowsLoginModel(UserAuthenticationService auth, AuditService audit, SettingsService settings) : PageModel
{
    public string? ReturnUrl { get; private set; }

    /// <summary>
    /// <paramref name="test"/>: jen ověřit identitu a výsledek ukázat v Nastavení (bez změny přihlášení správce).
    /// </summary>
    public async Task<IActionResult> OnGetAsync(string? returnUrl = null, string? test = null)
    {
        ReturnUrl = returnUrl is { Length: > 0 } && Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        var isTest = test is "1" or "true";

        if (!await settings.GetBoolAsync(SettingKeys.SsoEnabled))
            return Fail("disabled", isTest);

        var result = await HttpContext.AuthenticateAsync(NegotiateDefaults.AuthenticationScheme);
        var identity = result.Succeeded ? result.Principal?.Identity : null;
        if (identity?.Name is not { Length: > 0 } identityName)
        {
            // Prohlížeč už token poslal a přesto identita není — znovu vyzývat by točilo smyčku.
            if (Request.Headers.Authorization.Count > 0)
                return Fail("failed", isTest);

            // 401 + WWW-Authenticate: Negotiate. Tělo odpovědi uvidí jen prohlížeč, který výzvu
            // nezvládne (PC mimo doménu, zrušený dialog) — proto obsahuje odkaz na formulář.
            HttpContext.Features.Get<IStatusCodePagesFeature>()?.Enabled = false;
            await HttpContext.ChallengeAsync(NegotiateDefaults.AuthenticationScheme);
            return Page();
        }

        if (isTest)
        {
            TempData["SsoTestResult"] = $"OK — identita „{identityName}“, protokol {identity.AuthenticationType ?? "Negotiate"}.";
            TempData["ActiveSection"] = "sso";
            return RedirectToPage("/Admin/Settings");
        }

        var user = await auth.SignInWindowsIdentityAsync(identityName);
        if (user is null)
        {
            await audit.LogAsync(identityName, "login-failed", details: "windows");
            return Fail("account", isTest);
        }

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            AppUserPrincipal.Create(user, "windows"));
        await audit.LogAsync(user.UserName, "login", details: $"windows: {identityName}");

        return LocalRedirect(ReturnUrl ?? "/");
    }

    private IActionResult Fail(string code, bool test)
    {
        if (test)
        {
            TempData["SsoTestResult"] = code switch
            {
                "disabled" => "Přihlášení Windows není zapnuté — uložte sekci se zaškrtnutým „Povolit“.",
                _ => "Selhalo — prohlížeč neposlal platný token nebo ho server neověřil (viz log acs-web: journalctl -u acs-web).",
            };
            TempData["ActiveSection"] = "sso";
            return RedirectToPage("/Admin/Settings");
        }

        return Redirect(FailureRedirectUrl(HttpContext, code));
    }

    /// <summary>Kam poslat uživatele, když přihlášení Windows selže (i z události Negotiate handleru).</summary>
    public static string FailureRedirectUrl(HttpContext context, string code)
    {
        if (context.Request.Query["test"].ToString() is "true" or "1" or "True")
            return "/Admin/Settings?ssoTest=" + Uri.EscapeDataString(code) + "#s-sso";

        var url = "/Account/Login?manual=1&sso=" + Uri.EscapeDataString(code);
        var returnUrl = context.Request.Query["returnUrl"].ToString();
        if (returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal))
            url += "&returnUrl=" + Uri.EscapeDataString(returnUrl);
        return url;
    }
}
