using System.DirectoryServices.Protocols;
using Acs.Infrastructure.Auth;
using Acs.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace Acs.Infrastructure.Sync;

/// <summary>
/// Zdroj zaměstnanců z Active Directory. Cílový řadič vrací <see cref="DcLocator"/>
/// (DNS SRV, failover); bind přes servisní účet.
///
/// Velké domény (tisíce účtů) vyžadují ošetření:
/// <list type="bullet">
///   <item><b>stránkování</b> (PageResultRequestControl) — AD vrací max ~1000 záznamů na dotaz,</item>
///   <item><b>SizeLimit = 0</b> na požadavku, aby limit řídilo jen stránkování,</item>
///   <item><b>vypnuté honění referralů</b> — jinak dotaz padá na nedostupných doménách,</item>
///   <item><b>delší timeout</b> a odolnost vůči <c>SizeLimitExceeded</c>: co se stihlo načíst,
///     se vrátí jako dílčí výsledek místo úplného selhání,</item>
///   <item>volitelné omezení na organizační jednotku (Base DN) a vlastní filtr.</item>
/// </list>
/// </summary>
public class LdapEmployeeSource(
    SettingsService settings, DcLocator dcLocator, ILogger? logger = null) : IEmployeeSource
{
    private const string DefaultFilter =
        "(&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))";

    public async Task<IReadOnlyList<EmployeeRecord>> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            return await FetchOnceAsync(ct);
        }
        catch (LdapException ex) when (LdapAuthenticator.IsConnectivityError(ex))
        {
            logger?.LogWarning(ex, "LDAP: řadič nedostupný, zkouším jiný.");
            DcLocator.Invalidate();
            return await FetchOnceAsync(ct);
        }
    }

    private async Task<IReadOnlyList<EmployeeRecord>> FetchOnceAsync(CancellationToken ct)
    {
        var server = await dcLocator.GetActiveServerAsync(ct);
        var useSsl = await settings.GetBoolAsync(SettingKeys.LdapUseSsl, true, ct);
        var port = await settings.GetIntAsync(SettingKeys.LdapPort, useSsl ? 636 : 389, ct);
        var baseDn = await settings.GetAsync(SettingKeys.LdapBaseDn, ct)
            ?? throw new InvalidOperationException("Není nastaveno Base DN (Nastavení → Active Directory).");
        var bindUser = await settings.GetAsync(SettingKeys.LdapBindUser, ct)
            ?? throw new InvalidOperationException("Není nastaven servisní účet pro LDAP bind (Nastavení → Active Directory).");
        var bindPassword = await settings.GetAsync(SettingKeys.LdapBindPassword, ct)
            ?? throw new InvalidOperationException("Není nastaveno heslo servisního účtu (Nastavení → Active Directory).");
        var filter = await settings.GetAsync(SettingKeys.EmployeeLdapFilter, ct) ?? DefaultFilter;
        var pageSize = Math.Clamp(await settings.GetIntAsync(SettingKeys.EmployeeLdapPageSize, 500, ct), 50, 1000);
        var timeoutMinutes = Math.Clamp(await settings.GetIntAsync(SettingKeys.EmployeeLdapTimeoutMinutes, 10, ct), 1, 120);

        using var connection = LdapAuthenticator.CreateConnection(server, port, useSsl, bindUser, bindPassword);
        connection.Timeout = TimeSpan.FromMinutes(timeoutMinutes);
        // Referraly na jiné domény by u velkých forestů dotaz shodily.
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        connection.Bind();

        var result = new List<EmployeeRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pageControl = new PageResultRequestControl(pageSize);
        var page = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var request = new SearchRequest(baseDn, filter, SearchScope.Subtree,
                "sAMAccountName", "givenName", "sn", "displayName", "mail", "department",
                "employeeID", "employeeNumber", "title", "physicalDeliveryOfficeName");
            request.SizeLimit = 0;            // limit řídí stránkování, ne server-side cap
            request.TimeLimit = TimeSpan.FromMinutes(timeoutMinutes);
            request.Controls.Add(pageControl);
            // DomainScope = nehledat mimo doménu (jinak dotaz padá na referralech).
            request.Controls.Add(new SearchOptionsControl(
                System.DirectoryServices.Protocols.SearchOption.DomainScope));

            SearchResponse response;
            try
            {
                response = (SearchResponse)connection.SendRequest(request, TimeSpan.FromMinutes(timeoutMinutes));
            }
            catch (DirectoryOperationException ex) when (ex.Response is SearchResponse partial)
            {
                // Typicky SizeLimitExceeded — co dorazilo, zpracujeme a končíme.
                logger?.LogWarning(ex,
                    "LDAP: dotaz ukončen serverem ({Result}), pokračuji s dílčím výsledkem ({Count} záznamů).",
                    partial.ResultCode, result.Count);
                Collect(partial, result, seen);
                break;
            }

            Collect(response, result, seen);
            page++;
            logger?.LogInformation("LDAP: načtena stránka {Page} ({Total} zaměstnanců celkem).", page, result.Count);

            var cookie = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie;
            if (cookie is null || cookie.Length == 0)
                break;
            pageControl.Cookie = cookie;
        }

        logger?.LogInformation("LDAP: import dokončen — {Count} zaměstnanců z {Pages} stránek.", result.Count, page);
        return result;
    }

    private static void Collect(SearchResponse response, List<EmployeeRecord> result, HashSet<string> seen)
    {
        foreach (SearchResultEntry entry in response.Entries)
        {
            var sam = GetAttr(entry, "sAMAccountName");
            if (string.IsNullOrWhiteSpace(sam) || !seen.Add(sam))
                continue;   // servisní účty bez sAMAccountName a duplicity ze stránkování

            var firstName = GetAttr(entry, "givenName");
            var lastName = GetAttr(entry, "sn");
            if (firstName is null && lastName is null)
            {
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
                Department: GetAttr(entry, "department") ?? GetAttr(entry, "physicalDeliveryOfficeName"),
                AdAccount: sam,
                CardNumber: null));   // karty se dotahují samostatně ze SQL
        }
    }

    private static string? GetAttr(SearchResultEntry entry, string name)
        => entry.Attributes[name] is { Count: > 0 } attr ? attr[0]?.ToString() : null;
}
