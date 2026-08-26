using System.Security.Claims;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Auth;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Account;

public class ChangePasswordModel(AcsDbContext db, AuditService audit) : PageModel
{
    [BindProperty] public string CurrentPassword { get; set; } = "";
    [BindProperty] public string NewPassword { get; set; } = "";
    [BindProperty] public string ConfirmPassword { get; set; } = "";

    public bool MustChange => User.HasClaim("must_change_password", "1");
    public string? ErrorMessage { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == User.Identity!.Name && u.IsLocal);
        if (user is null)
        {
            ErrorMessage = "Heslo lze měnit pouze u lokálních účtů (AD hesla spravuje doména).";
            return Page();
        }

        if (user.PasswordHash is null || !PasswordHasher.Verify(CurrentPassword, user.PasswordHash))
        {
            ErrorMessage = "Současné heslo není správné.";
            return Page();
        }

        if (NewPassword.Length < 12)
        {
            ErrorMessage = "Nové heslo musí mít alespoň 12 znaků.";
            return Page();
        }

        if (NewPassword != ConfirmPassword)
        {
            ErrorMessage = "Nová hesla se neshodují.";
            return Page();
        }

        user.PasswordHash = PasswordHasher.Hash(NewPassword);
        user.MustChangePassword = false;
        await db.SaveChangesAsync();
        await audit.LogAsync(user.UserName, "password-changed");

        // Nové přihlášení bez příznaku must_change_password.
        var identity = (ClaimsIdentity)User.Identity!;
        var claims = identity.Claims.Where(c => c.Type != "must_change_password").ToList();
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));

        return RedirectToPage("/Index");
    }
}
