using System.DirectoryServices.Protocols;
using System.Text;

namespace Acs.Infrastructure.Sync;

/// <summary>
/// Čtení hodnot z LDAP atributů a mapování na zaměstnance.
///
/// Odděleno od <see cref="LdapEmployeeSource"/> ze dvou důvodů: sdílí to
/// diagnostický výpis atributů a hlavně to jde otestovat bez skutečného
/// doménového řadiče.
/// </summary>
public static class LdapAttributes
{
    /// <summary>Delší hodnoty se do výpisu nevypisují celé (fotky, certifikáty).</summary>
    private const int MaxDumpLength = 512;

    /// <summary>
    /// Použitelné textové hodnoty atributu — otrimované, bez prázdných.
    ///
    /// <c>SearchResultEntry</c> vrací hodnotu jako <c>string</c>, ale u dat, která
    /// nejsou platné UTF-8 (objectSid, GUID, fotky), vrátí <c>byte[]</c>. Volání
    /// <c>ToString()</c> na tom dá doslova „System.Byte[]“ — a taková hodnota se
    /// pak uloží jako osobní číslo. Binární hodnoty se proto přeskakují.
    /// </summary>
    public static string[] ReadTextValues(DirectoryAttribute? attribute)
    {
        if (attribute is null || attribute.Count == 0)
            return [];

        var values = new List<string>(attribute.Count);
        for (var i = 0; i < attribute.Count; i++)
        {
            if (attribute[i] is string text && !string.IsNullOrWhiteSpace(text))
                values.Add(text.Trim());
        }

        return [.. values];
    }

    /// <summary>
    /// Hodnoty atributu pro výpis člověku — binární data se ukážou jako délka
    /// a hexadecimální náhled, ať je poznat, že tam něco je, a co.
    /// </summary>
    public static string[] DescribeValues(DirectoryAttribute? attribute)
    {
        if (attribute is null || attribute.Count == 0)
            return [];

        var values = new List<string>(attribute.Count);
        for (var i = 0; i < attribute.Count; i++)
        {
            switch (attribute[i])
            {
                case string text:
                    values.Add(text.Length > MaxDumpLength
                        ? $"{text[..MaxDumpLength]}… (celkem {text.Length} znaků)"
                        : text);
                    break;
                case byte[] bytes:
                    var preview = Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 16)));
                    values.Add($"«binárně, {bytes.Length} B: {preview}{(bytes.Length > 16 ? "…" : "")}»");
                    break;
                case { } other:
                    values.Add(other.ToString() ?? "");
                    break;
            }
        }

        return [.. values];
    }

    /// <summary>
    /// První použitelná hodnota z uvedených atributů v daném pořadí.
    ///
    /// Původní zápis <c>GetAttr("employeeID") ?? GetAttr("employeeNumber")</c>
    /// měl tichou past: když <c>employeeID</c> existoval a byl prázdný nebo jen
    /// mezery, vrátil prázdný řetězec — a ten není null, takže se na
    /// <c>employeeNumber</c> nikdy nepřešlo a osobní číslo zůstalo prázdné.
    /// </summary>
    public static string? FirstValue(Func<string, string[]> lookup, params string[] attributeNames)
    {
        foreach (var name in attributeNames)
        {
            var values = lookup(name);
            if (values.Length > 0)
                return values[0];
        }

        return null;
    }

    /// <summary>Který z atributů hodnotu doopravdy dodal (pro diagnostiku).</summary>
    public static string? SourceAttribute(Func<string, string[]> lookup, params string[] attributeNames)
        => attributeNames.FirstOrDefault(name => lookup(name).Length > 0);

    /// <summary>
    /// Escapování hodnoty do LDAP filtru podle RFC 4515 — bez toho by uživatel
    /// se závorkou nebo zpětným lomítkem v příjmení dotaz rozbil.
    /// </summary>
    public static string EscapeFilter(string value) => value
        .Replace(@"\", @"\5c").Replace("*", @"\2a").Replace("(", @"\28")
        .Replace(")", @"\29").Replace("\0", @"\00");

    /// <summary>Výchozí AD atribut s nadřízeným — DN objektu nadřízeného.</summary>
    public const string DefaultManagerAttribute = "manager";

    /// <summary>Atributy, které se pro sestavení zaměstnance dotahují z AD.</summary>
    public static string[] RequestedAttributes(
        IReadOnlyList<string> personalNumberAttributes, string managerAttribute = DefaultManagerAttribute) =>
    [
        "sAMAccountName", "givenName", "sn", "displayName", "mail",
        "department", "physicalDeliveryOfficeName", "distinguishedName", managerAttribute,
        .. personalNumberAttributes,
    ];

    /// <summary>
    /// Sestaví zaměstnance z atributů jednoho záznamu. Vrací null u záznamů bez
    /// <c>sAMAccountName</c> — to jsou typicky servisní a systémové objekty.
    /// Nadřízený se zatím uloží „surově“ (<see cref="EmployeeRecord.ManagerRaw"/> = DN);
    /// na <c>ExternalId</c> ho převede <see cref="ResolveManagers"/> až nad celou dávkou.
    /// </summary>
    public static EmployeeRecord? MapEmployee(
        Func<string, string[]> lookup, IReadOnlyList<string> personalNumberAttributes,
        string managerAttribute = DefaultManagerAttribute)
    {
        var sam = FirstValue(lookup, "sAMAccountName");
        if (string.IsNullOrWhiteSpace(sam))
            return null;

        var firstName = FirstValue(lookup, "givenName");
        var lastName = FirstValue(lookup, "sn");
        if (firstName is null && lastName is null)
        {
            // Účty bez rozpadlého jména — rozdělí se zobrazované jméno.
            var display = FirstValue(lookup, "displayName") ?? sam;
            var parts = display.Split(' ', 2);
            firstName = parts.Length > 1 ? parts[0] : "";
            lastName = parts.Length > 1 ? parts[1] : display;
        }

        return new EmployeeRecord(
            ExternalId: sam,
            PersonalNumber: FirstValue(lookup, [.. personalNumberAttributes]),
            FirstName: firstName ?? "",
            LastName: lastName ?? "",
            Email: FirstValue(lookup, "mail"),
            Department: FirstValue(lookup, "department", "physicalDeliveryOfficeName"),
            AdAccount: sam,
            CardNumber: null,   // karty se dotahují samostatně ze SQL
            ManagerExternalId: null,
            ManagerRaw: FirstValue(lookup, managerAttribute));
    }

    /// <summary>
    /// Převede nadřízené z DN (hodnota atributu <c>manager</c>) na <c>ExternalId</c>
    /// (sAMAccountName) podle DN ostatních záznamů dávky. Nadřízený mimo dávku
    /// (jiná OU, zablokovaný účet, mimo filtr) zůstane nespárovaný — DN zůstává
    /// v <see cref="EmployeeRecord.ManagerRaw"/> pro diagnostiku. Hodnota, která není DN
    /// (např. rovnou sAMAccountName z jiného atributu), se porovná s účty přímo.
    /// </summary>
    public static IReadOnlyList<EmployeeRecord> ResolveManagers(
        IReadOnlyList<(EmployeeRecord Record, string? Dn)> batch)
    {
        var byDn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byAccount = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (record, dn) in batch)
        {
            byAccount.Add(record.ExternalId);
            if (!string.IsNullOrWhiteSpace(dn))
                byDn.TryAdd(dn.Trim(), record.ExternalId);
        }

        var result = new List<EmployeeRecord>(batch.Count);
        foreach (var (record, _) in batch)
        {
            var raw = record.ManagerRaw?.Trim();
            string? managerId = null;
            if (!string.IsNullOrEmpty(raw))
            {
                if (byDn.TryGetValue(raw, out var viaDn))
                    managerId = viaDn;
                else if (!raw.Contains('=') && byAccount.Contains(raw))
                    managerId = raw;
            }

            // Sám sobě nadřízeným (chyba v AD) → bez nadřízeného.
            if (managerId is not null && managerId.Equals(record.ExternalId, StringComparison.OrdinalIgnoreCase))
                managerId = null;

            result.Add(record with { ManagerExternalId = managerId });
        }

        return result;
    }

    /// <summary>Čitelný tvar DN nadřízeného pro diagnostiku („CN=Novák Jan,OU=…“ → „Novák Jan“).</summary>
    public static string DescribeManager(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        var first = raw.Split(',')[0];
        return first.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) ? first[3..].Replace(@"\", "") : raw;
    }

    /// <summary>Popis mapování pro diagnostiku — co se z kterého atributu bere.</summary>
    public static IReadOnlyList<(string Attribute, string MapsTo)> MappingDescription(
        IReadOnlyList<string> personalNumberAttributes, string managerAttribute = DefaultManagerAttribute)
    {
        var mapping = new List<(string, string)>
        {
            ("sAMAccountName", "účet AD a klíč pro párování (ExternalId)"),
        };

        foreach (var (name, index) in personalNumberAttributes.Select((n, i) => (n, i)))
        {
            mapping.Add((name, index == 0
                ? "osobní číslo"
                : $"osobní číslo (záloha {index}. v pořadí)"));
        }

        mapping.AddRange(
        [
            ("givenName", "jméno"),
            ("sn", "příjmení"),
            ("displayName", "jméno a příjmení, když givenName i sn chybí"),
            ("mail", "e-mail"),
            ("department", "oddělení"),
            ("physicalDeliveryOfficeName", "oddělení, když department chybí"),
            (managerAttribute, "nadřízený (DN → zaměstnanec se stejným účtem; schvalování nadřízeným)"),
        ]);

        return mapping;
    }

    /// <summary>Rozparsuje seznam atributů z nastavení („employeeID, employeeNumber“).</summary>
    public static string[] ParseAttributeList(string? configured, params string[] fallback)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return fallback;

        var parsed = configured
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return parsed.Length > 0 ? parsed : fallback;
    }

    /// <summary>Vyhledávací funkce nad jedním záznamem z AD.</summary>
    public static Func<string, string[]> TextLookup(SearchResultEntry entry)
        => name => ReadTextValues(entry.Attributes[name]);
}
