using System.Text.RegularExpressions;
using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers.Com;

/// <summary>
/// Zprávy WIN-PAK komunikačního serveru ve formátu <c>&lt;NLZ&gt;…&lt;/NLZ&gt;</c>.
/// Stejný formát používá zpětné volání <c>GotMessage</c> (události a alarmy),
/// <c>GetDoorStatus2</c> i <c>IsConnected2</c>.
///
/// Není to validní XML (příručka sama uvádí ukázky s nespárovanými značkami,
/// např. <c>&lt;Connected&gt;…&lt;/connected&gt;</c>), proto se čte regulárním
/// výrazem a ne XML parserem.
/// </summary>
public static partial class NlzMessage
{
    // Obal <NLZ> je z hledání značek vyloučený — jinak by ho nenasytné .*? spolklo celé.
    [GeneratedRegex(@"<(?!NLZ\b)(?<tag>[A-Za-z_][A-Za-z0-9_]*)>(?<value>.*?)</\k<tag>>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"<NLZ>(?<body>.*?)</NLZ>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex BlockPattern();

    /// <summary>Rozdělí vstup na jednotlivé bloky <c>&lt;NLZ&gt;</c> (jedna odpověď jich může nést víc).</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> ParseBlocks(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return [];

        var blocks = BlockPattern().Matches(message)
            .Select(m => ParseTags(m.Groups["body"].Value))
            .Where(tags => tags.Count > 0)
            .ToList();

        // Bez obalu <NLZ> zkusíme přečíst značky přímo — některá volání ho vynechávají.
        if (blocks.Count == 0 && ParseTags(message) is { Count: > 0 } bare)
            blocks.Add(bare);

        return blocks;
    }

    /// <summary>Přečte značky jednoho bloku. Opakovaná značka vyhrává tou první (viz duplicitní &lt;Account&gt; v příručce).</summary>
    public static IReadOnlyDictionary<string, string> ParseTags(string block)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TagPattern().Matches(block))
            tags.TryAdd(match.Groups["tag"].Value, match.Groups["value"].Value.Trim());

        return tags;
    }

    /// <summary>Tříhodnotový příznak WIN-PAKu: 0 = ne, 1 = ano, -1 = neznámo.</summary>
    public static bool? ReadTriState(IReadOnlyDictionary<string, string> tags, string tag)
        => tags.TryGetValue(tag, out var raw) && int.TryParse(raw, out var value) && value >= 0
            ? value != 0
            : null;

    public static string? ReadString(IReadOnlyDictionary<string, string> tags, string tag)
        => tags.TryGetValue(tag, out var value) && value.Length > 0 ? value : null;

    /// <summary>Stav dveří z odpovědi <c>GetDoorStatus2</c>.</summary>
    public static DoorStatusDto ParseDoorStatus(long hid, string? message)
    {
        var tags = ParseBlocks(message).FirstOrDefault()
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return new DoorStatusDto(
            Hid: ReadString(tags, "ADV_Hid") ?? hid.ToString(),
            DeviceName: ReadString(tags, "ADV_DeviceName"),
            IsOpen: ReadTriState(tags, "Door_IsOpen"),
            IsShunted: ReadTriState(tags, "Door_IsShunted"),
            ForcedOpen: ReadTriState(tags, "Door_ForcedOpen"),
            Ajar: ReadTriState(tags, "Door_Ajar"),
            Account: ReadString(tags, "Account"),
            SubAccount: ReadString(tags, "SubAccount"));
    }

    /// <summary>Stavy serverů z odpovědi <c>IsConnected2</c>.</summary>
    public static IReadOnlyList<ServerStatusDto> ParseServerStatus(string? message)
        => ParseBlocks(message)
            .Select(tags => new ServerStatusDto(
                ServerId: ReadString(tags, "SrvId") ?? "",
                ServerName: ReadString(tags, "Server") ?? "",
                Connected: ReadTriState(tags, "Connected") ?? false,
                ServerType: ReadString(tags, "SerType")))
            .Where(s => s.ServerId.Length > 0 || s.ServerName.Length > 0)
            .ToList();
}

/// <summary>Událost nebo alarm z panelu (zpětné volání <c>GotMessage</c>).</summary>
public record WinPakEvent(
    bool IsAlarm,
    int? EventId,
    string? Hid,
    string? Status,
    string? CardNumber,
    string? FullName,
    string? ReaderPoint,
    string? Account,
    string? SubAccount,
    DateTime? At)
{
    public static WinPakEvent FromTags(IReadOnlyDictionary<string, string> tags)
    {
        var index = NlzMessage.ReadString(tags, "Idx");
        var date = NlzMessage.ReadString(tags, "Date");
        var time = NlzMessage.ReadString(tags, "Time");

        return new WinPakEvent(
            // <Idx> větší než 0 znamená alarm, -1 běžnou událost.
            IsAlarm: int.TryParse(index, out var idx) && idx > 0,
            EventId: int.TryParse(NlzMessage.ReadString(tags, "EventID"), out var eventId) ? eventId : null,
            Hid: NlzMessage.ReadString(tags, "HID"),
            Status: NlzMessage.ReadString(tags, "Status"),
            CardNumber: NlzMessage.ReadString(tags, "CardNumber"),
            FullName: NlzMessage.ReadString(tags, "FullName"),
            ReaderPoint: NlzMessage.ReadString(tags, "RP"),
            Account: NlzMessage.ReadString(tags, "Account"),
            SubAccount: NlzMessage.ReadString(tags, "SubAccount"),
            At: DateTime.TryParse($"{date} {time}".Trim(), out var at) ? at : null);
    }

    public static IReadOnlyList<WinPakEvent> Parse(string? message)
        => NlzMessage.ParseBlocks(message).Select(FromTags).ToList();
}
