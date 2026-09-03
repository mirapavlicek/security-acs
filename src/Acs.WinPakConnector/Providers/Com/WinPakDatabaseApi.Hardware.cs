using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers.Com;

/// <summary>Panely, jejich výstupy a skupiny, přístupové oblasti a konfigurace čteček.</summary>
public sealed partial class WinPakDatabaseApi
{
    public IReadOnlyList<PanelDto> GetPanels()
        => CallList("GetPanelsByAcctID", MapPanel, AccountId, null);

    private static PanelDto MapPanel(IComDispatch panel) => new(
        ComValue.ToStringOrEmpty(panel.GetProperty("DeviceID")),
        ComValue.ToStringOrEmpty(panel.GetProperty("DeviceName")),
        ComValue.ToStringOrNull(panel.GetProperty("DeviceDesc")),
        ComValue.ToStringOrNull(panel.GetProperty("DeviceType")));

    public IReadOnlyList<PanelPointDto> GetPanelOutputs(long panelId)
        => CallList("GetOutputsByPanelID", MapPanelPoint, AccountId, panelId, null);

    public IReadOnlyList<PanelPointDto> GetPanelGroups(long panelId)
        => CallList("GetGroupsByPanelID", MapPanelPoint, AccountId, panelId, null);

    private static PanelPointDto MapPanelPoint(IComDispatch point) => new(
        ComValue.ToStringOrEmpty(point.GetProperty("DeviceID")),
        ComValue.ToStringOrEmpty(point.GetProperty("DeviceName")),
        ComValue.ToStringOrNull(point.GetProperty("DeviceDesc")));

    /// <summary>Zda má panel zapnutou volbu skupin (<c>IsGroupChecked</c>).</summary>
    public bool IsPanelGroupChecked(long panelId)
        => ComValue.ToInt(Call("IsGroupChecked", (int)AccountId, (int)panelId, 0)[2]) != 0;

    public void ConfigureOutputTimeZone(long panelId, long outputId, string timeZoneId)
        => CallCardWrite("Nastavení časové zóny výstupu", "ConfigureOutputTimezone",
            AccountId, panelId, outputId, ComValue.ToLong(timeZoneId), 0);

    /// <summary>Varianta s rozlišením zamknout/odemknout (<c>ConfigureOutputTimezoneEx</c>).</summary>
    public void ConfigureOutputTimeZoneEx(long panelId, long outputId, int lockUnlock, string timeZoneId)
        => CallCardWrite("Nastavení časové zóny výstupu", "ConfigureOutputTimezoneEx",
            AccountId, panelId, outputId, lockUnlock, ComValue.ToLong(timeZoneId), 0);

    public void ConfigureGroupTimeZone(long panelId, long groupId, string timeZoneId)
        => CallCardWrite("Nastavení časové zóny skupiny", "ConfigureGroupTimezone",
            AccountId, panelId, groupId, ComValue.ToLong(timeZoneId), 0);

    public TimeZoneDto? GetAssociatedTimeZoneOfOutput(long panelId, long outputId)
    {
        var result = Call("GetAssociatedTimezoneOfOutput", AccountId, panelId, outputId, null);
        var raw = ComValue.AsEnumerable(result[3]).FirstOrDefault();
        return raw is null ? null : MapTimeZone(_com.Wrap(raw));
    }

    public TimeZoneDto? GetAssociatedTimeZoneOfOutputEx(long panelId, long outputId, int lockUnlock)
    {
        var result = Call("GetAssociatedTimezoneOfOutputEX", AccountId, panelId, outputId, lockUnlock, null);
        var raw = ComValue.AsEnumerable(result[4]).FirstOrDefault();
        return raw is null ? null : MapTimeZone(_com.Wrap(raw));
    }

    public TimeZoneDto? GetAssociatedTimeZoneOfGroup(long panelId, long groupId)
    {
        var result = Call("GetAssociatedTimezoneOfGroup", AccountId, panelId, groupId, null);
        var raw = ComValue.AsEnumerable(result[3]).FirstOrDefault();
        return raw is null ? null : MapTimeZone(_com.Wrap(raw));
    }

    // ---------- Přístupové oblasti ----------

    public IReadOnlyList<AccessAreaBranchDto> GetAccessAreaBranches()
        => CallList("GetAccessAreaBranchesByAccountName",
            branch => new AccessAreaBranchDto(
                ComValue.ToStringOrEmpty(branch.GetProperty("DeviceID")),
                ComValue.ToStringOrEmpty(branch.GetProperty("DeviceName"))),
            AccountName, null);

    public IReadOnlyList<ReaderDto> GetReadersInAccessAreaBranch(string branchName)
        => CallList("GetReadersInAccessAreaBranch",
            reader => new ReaderDto(
                ComValue.ToStringOrEmpty(reader.GetProperty("HWDeviceID")),
                ComValue.ToStringOrEmpty(reader.GetProperty("DeviceName")),
                ComValue.ToStringOrNull(reader.GetProperty("DeviceDesc")),
                PanelName: null,
                AccountName: AccountName,
                IsActive: true),
            AccountName, branchName, null);

    public IReadOnlyList<TimeZoneDto> GetAvailableTimeZonesOfBranch(string branchName)
        => CallList("GetAvailableTimezonesOfBranch", MapTimeZone, AccountName, branchName, null);

    // ---------- Skupiny čteček ----------

    public IReadOnlyList<PanelPointDto> GetAvailableGroupsOfReader(string readerName)
        => CallList("GetAvailableGroupsofReader", MapPanelPoint, readerName, null);

    /// <summary>Skupina, kterou má čtečka v dané přístupové úrovni.</summary>
    public string? GetAssociatedGroupOfReader(string accessLevelName, string readerName)
        => ComValue.ToStringOrNull(
            Call("GetAssociatedGroupofReader", accessLevelName, AccountName, readerName, 0)[3]);
}
