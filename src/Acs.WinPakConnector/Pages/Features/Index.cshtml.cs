using Acs.WinPakConnector.Providers;

namespace Acs.WinPakConnector.Pages.Features;

public class FeaturesIndexModel(WinPakProviderCache providers) : FeaturePageModel(providers)
{
    /// <summary>Jedna oblast API: kam vede, co umí a jestli to ACS v běžném provozu volá.</summary>
    public record Area(string Title, string Page, string Description, string UsedByAcs);

    public static readonly IReadOnlyList<Area> Areas =
    [
        new("Dveře a zařízení", "/Features/Doors",
            "Stav dveří, zamknutí a odemknutí, puls, režim dveří i NetAXS, alarmy a jejich potvrzení, "
            + "shunt, buffer, spínání výstupů a vlastní příkazy pro zařízení.",
            "Ne — ACS zatím dveře neovládá, přístupy jen přiděluje."),

        new("Panely", "/Features/Panels",
            "Výpis panelů s jejich výstupy a skupinami, přiřazení časových zón a skupin svátků, "
            + "inicializace panelu, hromadné zamknutí dveří a door schedule.",
            "Ne — konfigurace hardwaru se dělá ve WIN-PAKu."),

        new("Karty a držitelé", "/Features/Cards",
            "Hledání a zápis karty včetně NetAXS voleb, hromadné založení a rušení rozsahu karet, "
            + "správa držitelů, vyhledávání v databázi WIN-PAK, fotky a podpisy.",
            "Částečně — ACS čte držitele a zapisuje jim přístupové úrovně, zbytek ne."),

        new("Přístupové úrovně", "/Features/AccessLevels",
            "Detail úrovně a strom přístupů, zakládání, konfigurace čteček a vstupů, "
            + "zjištění dotčených karet a jejich přeřazení před zrušením úrovně.",
            "Částečně — ACS úrovně jen čte a přiřazuje kartám."),

        new("Číselníky", "/Features/Catalog",
            "Časové zóny včetně intervalů, kdo je používá a přeřazení na jinou zónu; "
            + "svátky a skupiny svátků.",
            "Ne — časové zóny a svátky spravuje WIN-PAK."),

        new("Systém a události", "/Features/System",
            "Systémové údaje instalace, účty, drobné dotazy na názvy, plány a šablony reportů, "
            + "odznaky, muster report, filtry událostí a živý výpis událostí z panelů.",
            "Ne — slouží pro správu a diagnostiku."),
    ];

    public void OnGet()
    {
    }
}
