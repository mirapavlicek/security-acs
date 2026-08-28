using System.DirectoryServices.Protocols;
using Acs.Infrastructure.Auth;
using Acs.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace Acs.Infrastructure.Sync;

/// <summary>
/// Parametry spojení pro výpis mimo nastavení aplikace — například když LDAP
/// jde přes SSH tunel z jiné sítě a databáze ACS o tom nemá vědět.
/// </summary>
public record LdapConnectionOptions(
    string Server,
    int Port,
    bool UseSsl,
    string BaseDn,
    string BindUser,
    string BindPassword,
    IReadOnlyList<string>? PersonalNumberAttributes = null);

/// <summary>Jeden atribut jednoho účtu z AD.</summary>
public record LdapAttributeDump(string Name, IReadOnlyList<string> Values, string? MapsTo)
{
    public string Joined => string.Join(" | ", Values);
}

/// <summary>
/// Jeden nalezený účet: co všechno o něm AD vrátilo a co z toho ACS udělá.
/// </summary>
public record LdapEntryDump(
    string Dn,
    IReadOnlyList<LdapAttributeDump> Attributes,
    EmployeeRecord? Mapped,
    string? PersonalNumberFrom);

public record LdapDumpResult(
    string Query,
    string Server,
    string BaseDn,
    string Filter,
    IReadOnlyList<string> PersonalNumberAttributes,
    IReadOnlyList<LdapEntryDump> Entries)
{
    public override string ToString()
        => Entries.Count == 0
            ? $"„{Query}“ v AD nenalezeno (server {Server}, Base DN {BaseDn})"
            : $"„{Query}“ — nalezeno účtů: {Entries.Count} (server {Server})";
}

/// <summary>
/// Vypíše, co Active Directory o konkrétním účtu vrací.
///
/// Bez tohohle se nedá poznat, proč se naimportovalo divné osobní číslo: každé
/// AD ho má jinde (employeeID, employeeNumber, extensionAttribute…) a hodnota
/// může být i binární. Výpis proto ukazuje surové atributy vedle toho, co z nich
/// ACS sestaví, včetně toho, ze kterého atributu osobní číslo doopravdy přišlo.
/// </summary>
public class LdapDiagnosticsService(
    SettingsService settings, DcLocator dcLocator, ILogger<LdapDiagnosticsService>? logger = null)
{
    /// <summary>Kolik účtů nejvíc vypsat — diagnostika, ne výpis domény.</summary>
    private const int MaxEntries = 5;

    /// <summary>
    /// Hledá podle čehokoli, co má člověk po ruce: přihlašovacího jména, osobního
    /// čísla i příjmení. Do jednoho z nich uživatel trefí, aniž by musel vědět,
    /// jak se který atribut jmenuje.
    /// </summary>
    /// <param name="options">
    /// Když je zadáno, použijí se tyto parametry místo nastavení aplikace. Slouží
    /// k ověření proti doménovému řadiči, ke kterému se ACS ještě nenastavil.
    /// </param>
    public async Task<LdapDumpResult> DumpAsync(
        string query, LdapConnectionOptions? options = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Zadejte přihlašovací jméno, osobní číslo nebo příjmení.", nameof(query));

        query = query.Trim();

        var connectionOptions = options ?? await LoadOptionsAsync(ct);
        var (server, port, useSsl, baseDn, bindUser, bindPassword, _) = connectionOptions;
        var personalNumberAttributes = connectionOptions.PersonalNumberAttributes
            ?? LdapAttributes.ParseAttributeList(
                await settings.GetAsync(SettingKeys.EmployeePersonalNumberAttribute, ct),
                LdapEmployeeSource.DefaultPersonalNumberAttributes);

        var filter = BuildFilter(query, personalNumberAttributes);

        using var connection = LdapAuthenticator.CreateConnection(server, port, useSsl, bindUser, bindPassword);
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        connection.Bind();

        // Bez seznamu atributů vrátí server všechny, které smí bind účet přečíst —
        // právě to je smysl výpisu: uvidí se i atributy, o kterých se neví.
        var request = new SearchRequest(baseDn, filter, SearchScope.Subtree);
        request.SizeLimit = MaxEntries;
        request.Controls.Add(new SearchOptionsControl(
            System.DirectoryServices.Protocols.SearchOption.DomainScope));

        SearchResponse response;
        try
        {
            response = (SearchResponse)connection.SendRequest(request, TimeSpan.FromMinutes(1));
        }
        catch (DirectoryOperationException ex) when (ex.Response is SearchResponse partial)
        {
            logger?.LogWarning(ex, "LDAP diagnostika: dotaz ukončen serverem ({Result}).", partial.ResultCode);
            response = partial;
        }

        var mapping = LdapAttributes.MappingDescription(personalNumberAttributes)
            .ToDictionary(m => m.Attribute, m => m.MapsTo, StringComparer.OrdinalIgnoreCase);

        var entries = new List<LdapEntryDump>();
        foreach (SearchResultEntry entry in response.Entries)
        {
            var attributes = new List<LdapAttributeDump>();
            foreach (string name in entry.Attributes.AttributeNames)
            {
                attributes.Add(new LdapAttributeDump(
                    name,
                    LdapAttributes.DescribeValues(entry.Attributes[name]),
                    mapping.GetValueOrDefault(name)));
            }

            // Namapované atributy nahoru, ať je hned vidět, co ACS bere.
            attributes.Sort((a, b) => (a.MapsTo is null).CompareTo(b.MapsTo is null) != 0
                ? (a.MapsTo is null).CompareTo(b.MapsTo is null)
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            var lookup = LdapAttributes.TextLookup(entry);
            entries.Add(new LdapEntryDump(
                entry.DistinguishedName,
                attributes,
                LdapAttributes.MapEmployee(lookup, personalNumberAttributes),
                LdapAttributes.SourceAttribute(lookup, [.. personalNumberAttributes])));
        }

        logger?.LogInformation("LDAP diagnostika „{Query}“: {Count} účtů.", query, entries.Count);
        return new LdapDumpResult(query, server, baseDn, filter, personalNumberAttributes, entries);
    }

    /// <summary>Parametry spojení z nastavení aplikace (Nastavení → Active Directory).</summary>
    private async Task<LdapConnectionOptions> LoadOptionsAsync(CancellationToken ct)
    {
        var useSsl = await settings.GetBoolAsync(SettingKeys.LdapUseSsl, true, ct);
        return new LdapConnectionOptions(
            Server: await dcLocator.GetActiveServerAsync(ct),
            Port: await settings.GetIntAsync(SettingKeys.LdapPort, useSsl ? 636 : 389, ct),
            UseSsl: useSsl,
            BaseDn: await settings.GetAsync(SettingKeys.LdapBaseDn, ct)
                ?? throw new InvalidOperationException("Není nastaveno Base DN (Nastavení → Active Directory)."),
            BindUser: await settings.GetAsync(SettingKeys.LdapBindUser, ct)
                ?? throw new InvalidOperationException("Není nastaven servisní účet pro LDAP bind (Nastavení → Active Directory)."),
            BindPassword: await settings.GetAsync(SettingKeys.LdapBindPassword, ct)
                ?? throw new InvalidOperationException("Není nastaveno heslo servisního účtu (Nastavení → Active Directory)."));
    }

    /// <summary>
    /// Filtr hledá zadanou hodnotu ve všech obvyklých identifikátorech. Osobní
    /// číslo se hledá i v nastavených atributech, takže když je jinde než ve
    /// výchozích, dotaz ho stejně najde.
    /// </summary>
    internal static string BuildFilter(string query, IReadOnlyList<string> personalNumberAttributes)
    {
        var value = LdapAttributes.EscapeFilter(query.Trim());
        var candidates = new List<string>
        {
            $"(sAMAccountName={value})",
            $"(userPrincipalName={value})",
            $"(sn={value})",
            $"(cn={value})",
            $"(displayName=*{value}*)",
            $"(mail={value})",
        };

        candidates.AddRange(personalNumberAttributes.Select(name => $"({name}={value})"));

        return $"(&(objectClass=user)(|{string.Concat(candidates)}))";
    }
}
