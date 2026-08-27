using System.DirectoryServices.Protocols;
using System.Net;
using Acs.Infrastructure.Settings;

namespace Acs.Infrastructure.Sync;

/// <summary>
/// Zdroj zaměstnanců z Active Directory. Používá LDAP nastavení z GUI
/// (server, LDAPS, base DN, servisní účet pro bind) a stránkované vyhledávání.
/// Mapování atributů: sAMAccountName → AD účet i ExternalId,
/// employeeID/employeeNumber → osobní číslo, givenName/sn → jméno,
/// mail → e-mail, department → oddělení.
/// </summary>
public class LdapEmployeeSource(SettingsService settings) : IEmployeeSource
{
    public async Task<IReadOnlyList<EmployeeRecord>> FetchAsync(CancellationToken ct = default)
    {
        var server = await settings.GetAsync(SettingKeys.LdapServer, ct)
            ?? throw new InvalidOperationException("Není nastaven LDAP server (Nastavení → Active Directory).");
        var port = await settings.GetIntAsync(SettingKeys.LdapPort, 636, ct);
        var useSsl = await settings.GetBoolAsync(SettingKeys.LdapUseSsl, true, ct);
        var baseDn = await settings.GetAsync(SettingKeys.LdapBaseDn, ct)
            ?? throw new InvalidOperationException("Není nastaveno Base DN (Nastavení → Active Directory).");
        var bindUser = await settings.GetAsync(SettingKeys.LdapBindUser, ct)
            ?? throw new InvalidOperationException("Není nastaven servisní účet pro LDAP bind (Nastavení → Active Directory).");
        var bindPassword = await settings.GetAsync(SettingKeys.LdapBindPassword, ct)
            ?? throw new InvalidOperationException("Není nastaveno heslo servisního účtu (Nastavení → Active Directory).");
        var filter = await settings.GetAsync(SettingKeys.EmployeeLdapFilter, ct)
            ?? "(&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))";

        var identifier = new LdapDirectoryIdentifier(server, port, fullyQualifiedDnsHostName: true, connectionless: false);
        using var connection = new LdapConnection(identifier)
        {
            AuthType = AuthType.Basic,
            Credential = new NetworkCredential(bindUser, bindPassword),
        };
        connection.SessionOptions.ProtocolVersion = 3;
        if (useSsl)
            connection.SessionOptions.SecureSocketLayer = true;
        connection.Bind();

        var result = new List<EmployeeRecord>();
        var pageControl = new PageResultRequestControl(500);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var request = new SearchRequest(baseDn, filter, SearchScope.Subtree,
                "sAMAccountName", "givenName", "sn", "displayName", "mail", "department",
                "employeeID", "employeeNumber");
            request.Controls.Add(pageControl);

            var response = (SearchResponse)connection.SendRequest(request);

            foreach (SearchResultEntry entry in response.Entries)
            {
                var sam = GetAttr(entry, "sAMAccountName");
                if (string.IsNullOrWhiteSpace(sam))
                    continue;

                var firstName = GetAttr(entry, "givenName");
                var lastName = GetAttr(entry, "sn");
                if (firstName is null && lastName is null)
                {
                    // fallback na displayName „Jméno Příjmení“
                    var display = GetAttr(entry, "displayName") ?? sam;
                    var parts = display.Split(' ', 2);
                    firstName = parts.Length > 1 ? parts[0] : "";
                    lastName = parts.Length > 1 ? parts[1] : display;
                }

                result.Add(new EmployeeRecord(
                    ExternalId: sam,
                    PersonalNumber: GetAttr(entry, "employeeID") ?? GetAttr(entry, "employeeNumber"),
                    FirstName: firstName ?? "",
                    LastName: lastName ?? "",
                    Email: GetAttr(entry, "mail"),
                    Department: GetAttr(entry, "department"),
                    AdAccount: sam,
                    CardNumber: null)); // karty se dotahují samostatně z SQL
            }

            var responseControl = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
            if (responseControl is null || responseControl.Cookie.Length == 0)
                break;
            pageControl.Cookie = responseControl.Cookie;
        }

        return result;
    }

    private static string? GetAttr(SearchResultEntry entry, string name)
        => entry.Attributes[name] is { Count: > 0 } attr ? attr[0]?.ToString() : null;
}
