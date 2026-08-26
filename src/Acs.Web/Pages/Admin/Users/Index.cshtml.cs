using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Auth;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Admin.Users;

public class IndexModel(AcsDbContext db, AuditService audit) : PageModel
{
    public List<AppUser> Users { get; private set; } = [];

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
        => Users = await db.Users.OrderBy(u => u.UserName).ToListAsync();

    public async Task<IActionResult> OnPostRolesAsync(int userId, string[] roles)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null)
            return NotFound();

        var newRoles = roles.Aggregate(AppRole.None,
            (acc, r) => Enum.TryParse<AppRole>(r, out var role) ? acc | role : acc);

        // Pojistka: nesmí zmizet poslední aktivní admin.
        if (user.Roles.HasFlag(AppRole.Admin) && !newRoles.HasFlag(AppRole.Admin)
            && !await db.Users.AnyAsync(u => u.Id != userId && u.IsActive && u.Roles.HasFlag(AppRole.Admin)))
        {
            ErrorMessage = "Nelze odebrat roli Admin poslednímu aktivnímu administrátorovi.";
            return RedirectToPage();
        }

        user.Roles = newRoles;
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "user-roles-changed", "AppUser", user.UserName, newRoles.ToString());
        Message = $"Role uživatele {user.UserName} uloženy.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleActiveAsync(int userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null)
            return NotFound();

        if (user.IsActive && user.Roles.HasFlag(AppRole.Admin)
            && !await db.Users.AnyAsync(u => u.Id != userId && u.IsActive && u.Roles.HasFlag(AppRole.Admin)))
        {
            ErrorMessage = "Nelze deaktivovat posledního aktivního administrátora.";
            return RedirectToPage();
        }

        user.IsActive = !user.IsActive;
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, user.IsActive ? "user-activated" : "user-deactivated",
            "AppUser", user.UserName);
        Message = $"Uživatel {user.UserName} {(user.IsActive ? "aktivován" : "deaktivován")}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(int userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null || !user.IsLocal)
            return NotFound();

        var newPassword = GeneratePassword();
        user.PasswordHash = PasswordHasher.Hash(newPassword);
        user.MustChangePassword = true;
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "user-password-reset", "AppUser", user.UserName);
        Message = $"Nové heslo uživatele {user.UserName}: {newPassword} (sdělte mu ho bezpečným kanálem; při přihlášení si ho změní).";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateAsync(string userName, string? displayName, string password)
    {
        userName = userName.Trim();
        if (await db.Users.AnyAsync(u => u.UserName == userName))
        {
            ErrorMessage = $"Uživatel {userName} už existuje.";
            return RedirectToPage();
        }

        db.Users.Add(new AppUser
        {
            UserName = userName,
            DisplayName = displayName,
            IsLocal = true,
            PasswordHash = PasswordHasher.Hash(password),
            MustChangePassword = true,
            Roles = AppRole.Employee,
        });
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "user-created", "AppUser", userName);
        Message = $"Lokální účet {userName} vytvořen.";
        return RedirectToPage();
    }

    private static string GeneratePassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789-";
        return new string(Enumerable.Range(0, 16)
            .Select(_ => chars[System.Security.Cryptography.RandomNumberGenerator.GetInt32(chars.Length)])
            .ToArray());
    }
}
