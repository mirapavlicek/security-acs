namespace Acs.WinPakConnector.Providers.Com;

/// <summary>Zápis do WIN-PAK skončil chybou — nese původní stavový kód z API.</summary>
public sealed class WinPakOperationException(string operation, int status, string reason)
    : Exception($"{operation} selhalo (WIN-PAK status {status}): {reason}")
{
    public int Status { get; } = status;
}

/// <summary>
/// Překlad číselných stavů, které vracejí zápisové metody Database API,
/// na srozumitelnou hlášku. Kódy jsou z příručky WIN-PAK 4.9 (kapitoly
/// AddCard/EditCard/AddUpdateCard a AddCardHolder/EditCardHolder).
/// </summary>
public static class WinPakStatus
{
    public const int Success = 0;

    private static readonly Dictionary<int, string> CardStatuses = new()
    {
        [0] = "operace proběhla v pořádku",
        [1] = "WIN-PAK operaci odmítl",
        [101] = "číslo karty už existuje",
        [102] = "neplatné číslo karty",
        [103] = "neplatný stav karty",
        [104] = "neplatná přístupová úroveň",
        [105] = "neplatný účet nebo podúčet",
        [106] = "neplatný rok aktivace karty",
        [107] = "neplatné datum aktivace karty",
        [108] = "neplatná délka čísla karty",
        [109] = "neplatný PIN",
        [110] = "neplatný typ přístupu",
        [111] = "neplatný NetAXS usage limit",
        [112] = "neplatná expirace",
        [113] = "neplatný NetAXS typ karty",
        [114] = "neplatné nastavení dočasné NetAXS karty",
        [115] = "neplatné nastavení omezené NetAXS karty",
    };

    private static readonly Dictionary<int, string> CardHolderStatuses = new()
    {
        [0] = "operace proběhla v pořádku",
        [1] = "WIN-PAK operaci odmítl",
        [105] = "neplatný účet nebo podúčet",
        [301] = "neplatné jméno nebo příjmení držitele (případně neplatné id držitele)",
        [302] = "jméno nebo příjmení držitele má nepovolený počet znaků",
    };

    public static string DescribeCard(int status)
        => CardStatuses.TryGetValue(status, out var text) ? text : $"neznámý stav {status}";

    public static string DescribeCardHolder(int status)
        => CardHolderStatuses.TryGetValue(status, out var text) ? text : $"neznámý stav {status}";

    /// <exception cref="WinPakOperationException">Stav není 0.</exception>
    public static void EnsureCardSucceeded(string operation, int status)
    {
        if (status != Success)
            throw new WinPakOperationException(operation, status, DescribeCard(status));
    }

    /// <exception cref="WinPakOperationException">Stav není 0.</exception>
    public static void EnsureCardHolderSucceeded(string operation, int status)
    {
        if (status != Success)
            throw new WinPakOperationException(operation, status, DescribeCardHolder(status));
    }
}
