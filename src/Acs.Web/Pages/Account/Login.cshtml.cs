using System.Security.Claims;
using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace Acs.Web.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting("login")]
public class LoginModel(UserAuthenticationService auth, AuditService audit) : PageModel
{
    [BindProperty]
    public string UserName { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    public string? ErrorMessage { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(UserName) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Zadejte uživatelské jméno a heslo.";
            return Page();
        }

        var user = await auth.AuthenticateAsync(UserName, Password);
        if (user is null)
        {
            await audit.LogAsync(UserName, "login-failed");
            ErrorMessage = "Neplatné přihlašovací údaje.";
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        };
        if (user.DisplayName is not null)
            claims.Add(new Claim("display_name", user.DisplayName));
        if (user.IsLocal)
            claims.Add(new Claim("is_local", "1"));   // heslo lze měnit jen u lokálních účtů, AD spravuje doména
        if (user.MustChangePassword)
            claims.Add(new Claim("must_change_password", "1"));

        foreach (var role in Enum.GetValues<AppRole>())
        {
            if (role != AppRole.None && user.Roles.HasFlag(role))
                claims.Add(new Claim(ClaimTypes.Role, role.ToString()));
        }

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));

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
        return RedirectToPage("/Account/Login");
    }
}
