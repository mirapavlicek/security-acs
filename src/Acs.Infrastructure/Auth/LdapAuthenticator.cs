using System.DirectoryServices.Protocols;
using System.Net;
using Acs.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace Acs.Infrastructure.Auth;

public record LdapUserInfo(string UserName, string? DisplayName, string? Email, IReadOnlyList<string> Groups);

/// <summary>
/// Ověření uživatele proti Active Directory přes LDAP(S) —
/// System.DirectoryServices.Protocols (funguje i na Linuxu).
/// Cílový řadič vrací <see cref="DcLocator"/> (DNS SRV `_ldap._tcp.dc._msdcs`);
/// při výpadku řadiče se cache zneplatní a ověření se zopakuje proti dalšímu
/// živému DC. Konfigurace se čte z nastavení v DB (editovatelné v GUI).
/// </summary>
public class LdapAuthenticator(SettingsService settings, DcLocator dcLocator, ILogger<LdapAuthenticator> logger)
{
    /// <summary>Ověří jméno+heslo bind-em do AD; při úspěchu vrátí info o uživateli.</summary>
    public virtual async Task<LdapUserInfo?> AuthenticateAsync(string userName, string password, CancellationToken ct = default)
    {
        if (!await settings.GetBoolAsync(SettingKeys.LdapEnabled, false, ct))
            return null;

        try
        {
            return await AuthenticateOnceAsync(userName, password, ct);
        }
        catch (LdapException ex) when (IsConnectivityError(ex))
        {
            // Řadič vypadl → zneplatnit cache lokátoru a zkusit jiný živý DC.
            logger.LogWarning(ex, "LDAP řadič neodpovídá — zkouším jiný DC.");
            DcLocator.Invalidate();
            try
            {
                return await AuthenticateOnceAsync(userName, password, ct);
            }
            catch (Exception retryEx)
            {
                logger.LogError(retryEx, "LDAP ověření selhalo i proti náhradnímu řadiči.");
                return null;
            }
        }
        catch (LdapException ex)
        {
            logger.LogWarning(ex, "LDAP ověření uživatele {User} selhalo.", userName);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "LDAP ověření nelze provést (žádný dostupný řadič).");
            return null;
        }
    }

    private async Task<LdapUserInfo?> AuthenticateOnceAsync(string userName, string password, CancellationToken ct)
    {
        var server = await dcLocator.GetActiveServerAsync(ct);
        var useSsl = await settings.GetBoolAsync(SettingKeys.LdapUseSsl, true, ct);
        var port = await settings.GetIntAsync(SettingKeys.LdapPort, useSsl ? 636 : 389, ct);
        var baseDn = await settings.GetAsync(SettingKeys.LdapBaseDn, ct) ?? "";
        var domain = await settings.GetAsync(SettingKeys.LdapDomain, ct);

        // 1) bind jako přihlašovaný uživatel (ověření hesla)
        var bindUser = domain is { Length: > 0 } && !userName.Contains('@') && !userName.Contains('\\')
            ? $"{userName}@{domain}"
            : userName;

        using var connection = CreateConnection(server, port, useSsl, bindUser, password);

        try
        {
            connection.Bind(); // vyhodí LdapException při špatném hesle
        }
        catch (LdapException ex) when (!IsConnectivityError(ex))
        {
            logger.LogWarning("LDAP: neplatné přihlášení uživatele {User} ({Message}).", userName, ex.Message);
            return null;
        }

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

    /// <summary>Sdílená tovární metoda LDAP spojení (používá ji i import zaměstnanců).</summary>
    public static LdapConnection CreateConnection(string server, int port, bool useSsl, string bindUser, string password)
    {
        var identifier = new LdapDirectoryIdentifier(server, port, fullyQualifiedDnsHostName: true, connectionless: false);
        var connection = new LdapConnection(identifier)
        {
            AuthType = AuthType.Basic,
            Credential = new NetworkCredential(bindUser, password),
        };
        connection.SessionOptions.ProtocolVersion = 3;
        if (useSsl)
            connection.SessionOptions.SecureSocketLayer = true;
        return connection;
    }

    /// <summary>Chyby spojení (server down/nedosažitelný) vs. chyby přihlášení.</summary>
    public static bool IsConnectivityError(LdapException ex)
        => ex.ErrorCode is 81 or 91 or 85 or 52; // ServerDown, ConnectError, TimeLimitExceeded, UnavailableCriticalExtension

    private static string? GetAttr(SearchResultEntry entry, string name)
        => entry.Attributes[name] is { Count: > 0 } attr ? attr[0]?.ToString() : null;

    private static string EscapeLdapFilter(string value) => value
        .Replace(@"\", @"\5c").Replace("*", @"\2a").Replace("(", @"\28")
        .Replace(")", @"\29").Replace("\0", @"\00");
}
