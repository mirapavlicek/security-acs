using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Acs.Infrastructure.Auth;

/// <summary>
/// Přihlášení: nejdřív lokální účty (admin), poté Active Directory — heslem (LDAP bind)
/// nebo účtem Windows (Negotiate: Kerberos / NTLM, viz <see cref="SignInWindowsIdentityAsync"/>).
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

        return await UpsertAdUserAsync(ldapUser.UserName, ldapUser, ct);
    }

    /// <summary>
    /// Přihlášení účtem Windows (Negotiate — Kerberos / NTLM): identitu už ověřil Kestrel
    /// proti doméně, tady se jen namapuje na uživatele ACS. Heslo aplikace nemá, proto se
    /// atributy a skupiny (role) načtou servisním účtem; když to nejde, zůstávají hodnoty z DB.
    /// Vrací <c>null</c>, je-li účet z nepovolené domény nebo uživatel neaktivní.
    /// </summary>
    public async Task<AppUser?> SignInWindowsIdentityAsync(string identityName, CancellationToken ct = default)
    {
        var account = WindowsAccountName.Parse(identityName);
        if (account.Account.Length == 0)
            return null;

        if (!account.IsDomainAllowed(await settings.GetAsync(SettingKeys.SsoAllowedDomains, ct)))
        {
            logger.LogWarning("Přihlášení Windows odmítnuto: účet {Identity} není z povolené domény.", identityName);
            return null;
        }

        // Účet stroje (KONTO$) nikdy nepatří uživateli.
        if (account.Account.EndsWith('$'))
            return null;

        var ldapUser = await ldap.LookupAsync(account.Account, ct);
        return await UpsertAdUserAsync(ldapUser?.UserName ?? account.Account, ldapUser, ct);
    }

    /// <summary>
    /// Založí / aktualizuje doménového uživatele ACS. Při prvním přihlášení se spáruje se
    /// zaměstnancem podle AD účtu. <paramref name="ldapUser"/> je <c>null</c>, když se
    /// atributy z AD nepodařilo načíst — pak se jméno, e-mail ani role nemění.
    /// </summary>
    private async Task<AppUser?> UpsertAdUserAsync(string userName, LdapUserInfo? ldapUser, CancellationToken ct)
    {
        // Kerberos vrací účet tak, jak ho zadal uživatel / jak je v ticketu — porovnává se bez ohledu na velikost písmen.
        var lowerName = userName.ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.UserName.ToLower() == lowerName, ct);
        if (user is { IsLocal: true })
        {
            // Jméno je obsazené lokálním účtem (např. „admin“) — ten se přihlašuje jen heslem,
            // doménový uživatel stejného jména by převzal jeho práva.
            logger.LogWarning("Doménové přihlášení {User} odmítnuto: jméno patří lokálnímu účtu.", userName);
            return null;
        }

        if (user is null)
        {
            var employee = await db.Employees.FirstOrDefaultAsync(
                e => e.AdAccount != null && e.AdAccount.ToLower() == lowerName, ct);
            user = new AppUser
            {
                UserName = userName,
                IsLocal = false,
                Roles = AppRole.Employee,
                EmployeeId = employee?.Id,
            };
            db.Users.Add(user);
        }

        if (!user.IsActive)
            return null;

        user.LastLoginAt = DateTime.UtcNow;

        if (ldapUser is not null)
        {
            user.DisplayName = ldapUser.DisplayName ?? user.DisplayName;
            user.Email = ldapUser.Email ?? user.Email;

            // Mapování AD skupin na role: je-li nakonfigurováno, role AD uživatele
            // se při každém přihlášení přepočítají podle členství ve skupinách.
            var mapText = await settings.GetAsync(SettingKeys.LdapGroupRoleMap, ct);
            if (!string.IsNullOrWhiteSpace(mapText))
                user.Roles = ResolveRolesFromGroups(ldapUser.Groups, mapText);
        }

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
    /// Pokud je zadáno <paramref name="bootstrapPassword"/> (nastavení
    /// <c>Admin:BootstrapPassword</c> / env <c>ACS_BOOTSTRAP_ADMIN_PASSWORD</c>),
    /// použije se jako počáteční heslo (operátor ho už zná, do logu se nevypisuje).
    /// Jinak se vygeneruje náhodné heslo a jednorázově se vypíše do logu.
    /// V obou případech je při prvním přihlášení vynucena změna hesla.
    /// </summary>
    public static async Task SeedLocalAdminAsync(
        AcsDbContext db, ILogger? logger = null, string? bootstrapPassword = null, CancellationToken ct = default)
    {
        if (await db.Users.AnyAsync(u => u.IsLocal && u.Roles.HasFlag(AppRole.Admin), ct))
            return;

        var operatorSupplied = !string.IsNullOrWhiteSpace(bootstrapPassword);
        var initialPassword = operatorSupplied ? bootstrapPassword! : GenerateInitialPassword();
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

        if (operatorSupplied)
        {
            var msg = "==== ACS: vytvořen lokální účet 'admin' s počátečním heslem zadaným v konfiguraci "
                    + "(ACS_BOOTSTRAP_ADMIN_PASSWORD); při prvním přihlášení bude vynucena změna ====";
            if (logger is not null) logger.LogWarning("{Message}", msg); else Console.WriteLine(msg);
            return;
        }

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
