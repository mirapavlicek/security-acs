namespace Acs.WinPakConnector.Providers.Com;

/// <summary>Konfigurace COM režimu (sekce <c>WinPak:Com</c> v appsettings).</summary>
public sealed class WinPakComOptions
{
    public const string SectionName = "WinPak:Com";

    /// <summary>Uživatel operátora WIN-PAK, pod kterým konektor volá API.</summary>
    public string UserName { get; set; } = "";

    public string Password { get; set; } = "";

    /// <summary>Doména operátora; prázdné u lokálních účtů WIN-PAK.</summary>
    public string Domain { get; set; } = "";

    /// <summary>Účet WIN-PAK, se kterým se pracuje (karty a držitelé jsou po účtech oddělení).</summary>
    public string AccountName { get; set; } = "";

    /// <summary>Podúčet; prázdné = výchozí.</summary>
    public string SubAccountName { get; set; } = "";

    /// <summary>Zda se má vedle databázového API použít i komunikační server (ovládání dveří).</summary>
    public bool EnableCommunicationServer { get; set; }

    /// <summary>Typ pohledu pro <c>InitServer</c> (wpviewTYPE); 4 = CommandOnly.</summary>
    public int CommViewType { get; set; } = 4;

    /// <summary>ProgID objektů; měnit jen pokud je instalace WIN-PAKu registruje jinak.</summary>
    public string ApplicationProgId { get; set; } = "NCIHelper.Application";

    public string CardHolderProgId { get; set; } = "NCIHelper.CardHolder";

    public string CommServerProgId { get; set; } = "ACCW.MTSCBServer";

    /// <summary>Kolik posledních událostí z komunikačního serveru se drží v paměti pro <c>GET /api/v1/events</c>.</summary>
    public int EventBufferSize { get; set; } = 500;
}
