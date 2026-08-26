using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Acs.Infrastructure.Auth;

/// <summary>
/// Přihlášení: nejdřív lokální účty (admin), poté Active Directory.
/// AD uživatelé se při prvním přihlášení automaticky založí v DB
/// a spárují se zaměstnancem podle AD účtu.
/// </summary>
public class UserAuthenticationService(
    AcsDbContext db,
    LdapAuthenticator ldap,
    ILogger<UserAuthenticationService> logger)
{
    public async Task<AppUser?> AuthenticateAsync(string userName, string password, CancellationToken ct = default)
    {
        var normalized = userName.Trim();

        var localUser = await db.Users.FirstOrDefaultAsync(
            u => u.UserName == normalized && u.IsLocal, ct);
        if (localUser is not null)
        {
            if (!localUser.IsActive || localUser.PasswordHash is null
                || !PasswordHasher.Verify(password, localUser.PasswordHash))
            {
                logger.LogWarning("Neúspěšné lokální přihlášení uživatele {User}.", normalized);
                return null;
            }

            localUser.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return localUser;
        }

        var ldapUser = await ldap.AuthenticateAsync(normalized, password, ct);
        if (ldapUser is null)
            return null;

        var user = await db.Users.FirstOrDefaultAsync(
            u => u.UserName == ldapUser.UserName && !u.IsLocal, ct);
        if (user is null)
        {
            var employee = await db.Employees.FirstOrDefaultAsync(
                e => e.AdAccount == ldapUser.UserName, ct);
            user = new AppUser
            {
                UserName = ldapUser.UserName,
                IsLocal = false,
                Roles = AppRole.Employee,
                EmployeeId = employee?.Id,
            };
            db.Users.Add(user);
        }

        if (!user.IsActive)
            return null;

        user.DisplayName = ldapUser.DisplayName ?? user.DisplayName;
        user.Email = ldapUser.Email ?? user.Email;
        user.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>Zajistí existenci lokálního admin účtu (první start aplikace).</summary>
    public static async Task SeedLocalAdminAsync(AcsDbContext db, CancellationToken ct = default)
    {
        if (await db.Users.AnyAsync(u => u.IsLocal && u.Roles.HasFlag(AppRole.Admin), ct))
            return;

        db.Users.Add(new AppUser
        {
            UserName = "admin",
            DisplayName = "Lokální administrátor",
            IsLocal = true,
            PasswordHash = PasswordHasher.Hash("admin"),
            MustChangePassword = true,
            Roles = AppRole.Admin,
        });
        await db.SaveChangesAsync(ct);
    }
}
