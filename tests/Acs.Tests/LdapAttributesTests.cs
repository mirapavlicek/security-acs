using System.DirectoryServices.Protocols;
using System.Text;
using Acs.Infrastructure.Sync;
using Xunit;

namespace Acs.Tests;

/// <summary>
/// Čtení atributů z AD a mapování na zaměstnance. Hlídá hlavně případy, kdy se
/// do osobního čísla dostane nesmysl — binární hodnota, prázdný řetězec blokující
/// záložní atribut, nebo neotrimované mezery.
/// </summary>
public class LdapAttributesTests
{
    private static readonly string[] Defaults = ["employeeID", "employeeNumber"];

    /// <summary>Vyhledávací funkce nad slovníkem — zastupuje jeden záznam z AD.</summary>
    private static Func<string, string[]> Lookup(Dictionary<string, string[]> attributes)
        => name => attributes.TryGetValue(name, out var values) ? values : [];

    // ---------- Čtení hodnot ----------

    [Fact]
    public void Binarni_hodnota_se_nepovazuje_za_text()
    {
        // objectSid a podobné atributy vrací byte[]; ToString() by dalo „System.Byte[]“
        // a přesně tohle by se uložilo jako osobní číslo.
        var attribute = new DirectoryAttribute("objectSid", new byte[] { 1, 5, 0, 0, 200 });

        var values = LdapAttributes.ReadTextValues(attribute);

        Assert.Empty(values);
    }

    [Fact]
    public void Binarni_hodnota_se_ve_vypisu_ukaze_jako_hex()
    {
        var attribute = new DirectoryAttribute("objectSid", new byte[] { 0x01, 0x05, 0xAB });

        var described = LdapAttributes.DescribeValues(attribute);

        var value = Assert.Single(described);
        Assert.Contains("binárně", value);
        Assert.Contains("3 B", value);
        Assert.Contains("0105AB", value);
    }

    [Fact]
    public void Textova_hodnota_se_otrimuje()
    {
        var attribute = new DirectoryAttribute("employeeID", "  13483  ");

        var values = LdapAttributes.ReadTextValues(attribute);

        Assert.Equal("13483", Assert.Single(values));
    }

    [Fact]
    public void Hodnota_jen_z_mezer_se_zahodi()
    {
        var attribute = new DirectoryAttribute("employeeID", "   ");

        Assert.Empty(LdapAttributes.ReadTextValues(attribute));
    }

    [Fact]
    public void Vice_hodnot_se_zachova_pro_vypis()
    {
        var attribute = new DirectoryAttribute("proxyAddresses", "SMTP:a@x.cz", "smtp:b@x.cz");

        Assert.Equal(2, LdapAttributes.DescribeValues(attribute).Length);
        Assert.Equal(2, LdapAttributes.ReadTextValues(attribute).Length);
    }

    [Fact]
    public void Dlouha_hodnota_se_ve_vypisu_zkrati()
    {
        var attribute = new DirectoryAttribute("info", new string('x', 900));

        var value = Assert.Single(LdapAttributes.DescribeValues(attribute));

        Assert.Contains("celkem 900 znaků", value);
        Assert.True(value.Length < 900);
    }

    [Fact]
    public void Chybejici_atribut_je_prazdny()
        => Assert.Empty(LdapAttributes.ReadTextValues(null));

    // ---------- Volba atributu ----------

    [Fact]
    public void Prazdny_prvni_atribut_neblokuje_zalozni()
    {
        // Původní zápis `GetAttr("employeeID") ?? GetAttr("employeeNumber")` vrátil
        // prázdný řetězec, který není null — na employeeNumber se nikdy nepřešlo.
        var lookup = Lookup(new()
        {
            ["employeeID"] = [],
            ["employeeNumber"] = ["13483"],
        });

        Assert.Equal("13483", LdapAttributes.FirstValue(lookup, Defaults));
    }

    [Fact]
    public void Prvni_neprazdny_atribut_ma_prednost()
    {
        var lookup = Lookup(new()
        {
            ["employeeID"] = ["A-99"],
            ["employeeNumber"] = ["13483"],
        });

        Assert.Equal("A-99", LdapAttributes.FirstValue(lookup, Defaults));
        Assert.Equal("employeeID", LdapAttributes.SourceAttribute(lookup, Defaults));
    }

    [Fact]
    public void Bez_hodnoty_neni_zdrojovy_atribut_zadny()
    {
        var lookup = Lookup(new());

        Assert.Null(LdapAttributes.FirstValue(lookup, Defaults));
        Assert.Null(LdapAttributes.SourceAttribute(lookup, Defaults));
    }

    // ---------- Mapování zaměstnance ----------

    [Fact]
    public void Mapovani_vezme_osobni_cislo_z_nastaveneho_atributu()
    {
        var lookup = Lookup(new()
        {
            ["sAMAccountName"] = ["13483"],
            ["givenName"] = ["Miroslav"],
            ["sn"] = ["Pavlíček"],
            ["employeeID"] = ["ignorovat"],
            ["extensionAttribute3"] = ["13483"],
        });

        var record = LdapAttributes.MapEmployee(lookup, ["extensionAttribute3"]);

        Assert.NotNull(record);
        Assert.Equal("13483", record.PersonalNumber);
        Assert.Equal("Pavlíček", record.LastName);
    }

    [Fact]
    public void Ucet_bez_samaccountname_se_preskoci()
    {
        var lookup = Lookup(new() { ["sn"] = ["Pavlíček"] });

        Assert.Null(LdapAttributes.MapEmployee(lookup, Defaults));
    }

    [Fact]
    public void Jmeno_se_rozdeli_z_displayName_kdyz_chybi_givenName_i_sn()
    {
        var lookup = Lookup(new()
        {
            ["sAMAccountName"] = ["13483"],
            ["displayName"] = ["Miroslav Pavlíček"],
        });

        var record = LdapAttributes.MapEmployee(lookup, Defaults);

        Assert.NotNull(record);
        Assert.Equal("Miroslav", record.FirstName);
        Assert.Equal("Pavlíček", record.LastName);
    }

    [Fact]
    public void Oddeleni_ma_zalohu_v_nazvu_kancelare()
    {
        var lookup = Lookup(new()
        {
            ["sAMAccountName"] = ["13483"],
            ["physicalDeliveryOfficeName"] = ["Chirurgie"],
        });

        var record = LdapAttributes.MapEmployee(lookup, Defaults);

        Assert.Equal("Chirurgie", record!.Department);
    }

    [Fact]
    public void Ucet_bez_osobniho_cisla_ho_ma_prazdne_ne_nesmyslne()
    {
        var lookup = Lookup(new()
        {
            ["sAMAccountName"] = ["13483"],
            ["sn"] = ["Pavlíček"],
        });

        var record = LdapAttributes.MapEmployee(lookup, Defaults);

        Assert.Null(record!.PersonalNumber);
    }

    // ---------- Nastavení atributů ----------

    [Theory]
    [InlineData("employeeNumber", new[] { "employeeNumber" })]
    [InlineData("extensionAttribute3, employeeNumber", new[] { "extensionAttribute3", "employeeNumber" })]
    [InlineData(" employeeID ;employeeNumber ", new[] { "employeeID", "employeeNumber" })]
    [InlineData("employeeID, employeeID", new[] { "employeeID" })]
    public void Seznam_atributu_se_rozparsuje(string configured, string[] expected)
        => Assert.Equal(expected, LdapAttributes.ParseAttributeList(configured, "vychozi"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ; ")]
    public void Prazdne_nastaveni_pouzije_vychozi_atributy(string? configured)
        => Assert.Equal(Defaults, LdapAttributes.ParseAttributeList(configured, Defaults));

    [Fact]
    public void Dotaz_si_rekne_i_o_nastavene_atributy()
    {
        var requested = LdapAttributes.RequestedAttributes(["extensionAttribute3"]);

        Assert.Contains("extensionAttribute3", requested);
        Assert.Contains("sAMAccountName", requested);
    }

    // ---------- Filtr ----------

    [Fact]
    public void Filtr_escapuje_zavorky_a_lomitka()
    {
        var escaped = LdapAttributes.EscapeFilter(@"Pav(lí)ček*\");

        Assert.DoesNotContain("(", escaped);
        Assert.DoesNotContain(")", escaped);
        Assert.DoesNotContain("*", escaped);
        Assert.Contains(@"\5c", escaped);
    }

    [Fact]
    public void Diagnosticky_filtr_hleda_i_v_nastavenych_atributech()
    {
        var filter = LdapDiagnosticsService.BuildFilter("13483", ["extensionAttribute3"]);

        Assert.Contains("(sAMAccountName=13483)", filter);
        Assert.Contains("(extensionAttribute3=13483)", filter);
        Assert.Contains("(sn=13483)", filter);
        Assert.StartsWith("(&(objectClass=user)(|", filter);
    }

    [Fact]
    public void Diagnosticky_filtr_snese_prijmeni_se_zavorkou()
    {
        var filter = LdapDiagnosticsService.BuildFilter("Pav(lí)ček", Defaults);

        // Neescapovaná závorka by dotaz rozbila na syntaktickou chybu.
        Assert.Contains(@"\28", filter);
        Assert.Contains(@"\29", filter);
        Assert.Equal(
            filter.Count(c => c == '('),
            filter.Count(c => c == ')'));
    }
}
