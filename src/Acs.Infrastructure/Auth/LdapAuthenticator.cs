using System.DirectoryServices.Protocols;
using System.Net;
using Acs.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace Acs.Infrastructure.Auth;

public record LdapUserInfo(string UserName, string? DisplayName, string? Email, IReadOnlyList<string> Groups);

/// <summary>
/// Ověření uživatele proti Active Directory přes LDAP(S).
/// Používá System.DirectoryServices.Protocols — funguje i na Linuxu.
/// Konfigurace se čte z nastavení v DB (editovatelné v GUI).
/// </summary>
public class LdapAuthenticator(SettingsService settings, ILogger<LdapAuthenticator> logger)
{
    /// <summary>Ověří jméno+heslo bind-em do AD; při úspěchu vrátí info o uživateli.</summary>
    public virtual async Task<LdapUserInfo?> AuthenticateAsync(string userName, string password, CancellationToken ct = default)
    {
        if (!await settings.GetBoolAsync(SettingKeys.LdapEnabled, false, ct))
            return null;

        var server = await settings.GetAsync(SettingKeys.LdapServer, ct);
        if (string.IsNullOrWhiteSpace(server))
            return null;

        var port = await settings.GetIntAsync(SettingKeys.LdapPort, 636, ct);
        var useSsl = await settings.GetBoolAsync(SettingKeys.LdapUseSsl, true, ct);
        var baseDn = await settings.GetAsync(SettingKeys.LdapBaseDn, ct) ?? "";
        var domain = await settings.GetAsync(SettingKeys.LdapDomain, ct);

        try
        {
            var identifier = new LdapDirectoryIdentifier(server, port, fullyQualifiedDnsHostName: true, connectionless: false);

            // 1) bind jako přihlašovaný uživatel (ověření hesla)
            var bindUser = domain is { Length: > 0 } && !userName.Contains('@') && !userName.Contains('\\')
                ? $"{userName}@{domain}"
                : userName;

            using var connection = new LdapConnection(identifier)
            {
                AuthType = AuthType.Basic,
                Credential = new NetworkCredential(bindUser, password),
            };
            connection.SessionOptions.ProtocolVersion = 3;
            if (useSsl)
                connection.SessionOptions.SecureSocketLayer = true;

            connection.Bind(); // vyhodí LdapException při špatném hesle

            // 2) dohledání atributů uživatele
            var filterTemplate = await settings.GetAsync(SettingKeys.LdapUserFilter, ct)
                ?? "(&(objectClass=user)(sAMAccountName={0}))";
            var samAccount = userName.Contains('@') ? userName.Split('@')[0] : userName.Split('\\').Last();
            var filter = string.Format(filterTemplate, EscapeLdapFilter(samAccount));

            var request = new SearchRequest(baseDn, filter, SearchScope.Subtree,
                "displayName", "mail", "memberOf", "sAMAccountName");
            var response = (SearchResponse)connection.SendRequest(request);

            if (response.Entries.Count == 0)
                return new LdapUserInfo(samAccount, null, null, []);

            var entry = response.Entries[0];
            var groups = new List<string>();
            if (entry.Attributes["memberOf"] is { } memberOf)
                groups.AddRange(memberOf.GetValues(typeof(string)).Cast<string>());

            return new LdapUserInfo(
                UserName: GetAttr(entry, "sAMAccountName") ?? samAccount,
                DisplayName: GetAttr(entry, "displayName"),
                Email: GetAttr(entry, "mail"),
                Groups: groups);
        }
        catch (LdapException ex)
        {
            logger.LogWarning(ex, "LDAP ověření uživatele {User} selhalo.", userName);
            return null;
        }
    }

    private static string? GetAttr(SearchResultEntry entry, string name)
        => entry.Attributes[name] is { Count: > 0 } attr ? attr[0]?.ToString() : null;

    private static string EscapeLdapFilter(string value) => value
        .Replace(@"\", @"\5c").Replace("*", @"\2a").Replace("(", @"\28")
        .Replace(")", @"\29").Replace("\0", @"\00");
}
