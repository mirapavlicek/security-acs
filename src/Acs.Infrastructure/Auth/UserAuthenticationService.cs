using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
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
    SettingsService settings,
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

        // Mapování AD skupin na role: je-li nakonfigurováno, role AD uživatele
        // se při každém přihlášení přepočítají podle členství ve skupinách.
        var mapText = await settings.GetAsync(SettingKeys.LdapGroupRoleMap, ct);
        if (!string.IsNullOrWhiteSpace(mapText))
            user.Roles = ResolveRolesFromGroups(ldapUser.Groups, mapText);

        await db.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>
    /// Řádky „NázevSkupiny=Role1,Role2“. Skupina se porovnává s celým DN
    /// i s jeho CN (case-insensitive). Každý AD uživatel má vždy roli Employee.
    /// </summary>
    public static AppRole ResolveRolesFromGroups(IReadOnlyList<string> groups, string mapText)
    {
        var groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dn in groups)
        {
            groupNames.Add(dn);
            var cn = dn.Split(',')[0];
            groupNames.Add(cn.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) ? cn[3..] : cn);
        }

        var roles = AppRole.Employee;
        foreach (var line in mapText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2 || !groupNames.Contains(parts[0].Trim()))
                continue;

            foreach (var roleName in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Enum.TryParse<AppRole>(roleName, ignoreCase: true, out var role))
                    roles |= role;
            }
        }

        return roles;
    }

    /// <summary>
    /// Zajistí existenci lokálního admin účtu (první start aplikace).
    /// Heslo se generuje náhodně a jednorázově vypíše do logu (žádné výchozí
    /// „admin/admin“); operátor ho přečte z <c>journalctl -u acs-web</c> a při
    /// prvním přihlášení je vynucena jeho změna.
    /// </summary>
    public static async Task SeedLocalAdminAsync(AcsDbContext db, ILogger? logger = null, CancellationToken ct = default)
    {
        if (await db.Users.AnyAsync(u => u.IsLocal && u.Roles.HasFlag(AppRole.Admin), ct))
            return;

        var initialPassword = GenerateInitialPassword();
        db.Users.Add(new AppUser
        {
            UserName = "admin",
            DisplayName = "Lokální administrátor",
            IsLocal = true,
            PasswordHash = PasswordHasher.Hash(initialPassword),
            MustChangePassword = true,
            Roles = AppRole.Admin,
        });
        await db.SaveChangesAsync(ct);

        var message = "==== ACS: vytvořen lokální účet 'admin' s počátečním heslem: {Password} "
                    + "(při prvním přihlášení bude vynucena změna) ====";
        if (logger is not null)
            logger.LogWarning(message, initialPassword);
        else
            Console.WriteLine(message.Replace("{Password}", initialPassword));
    }

    private static string GenerateInitialPassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
        return new string(Enumerable.Range(0, 20)
            .Select(_ => chars[System.Security.Cryptography.RandomNumberGenerator.GetInt32(chars.Length)])
            .ToArray());
    }
}
