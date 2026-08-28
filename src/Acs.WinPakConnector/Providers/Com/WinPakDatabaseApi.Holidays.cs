using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers.Com;

/// <summary>Svátky a skupiny svátků včetně jejich přiřazení panelům.</summary>
public sealed partial class WinPakDatabaseApi
{
    public HolidayDto? GetHoliday(string holidayId)
    {
        var result = Call("GetHolidayByID", ComValue.ToLong(holidayId), null);
        var raw = ComValue.AsEnumerable(result[1]).FirstOrDefault();
        return raw is null ? null : MapHoliday(_com.Wrap(raw));
    }

    private static HolidayDto MapHoliday(IComDispatch holiday) => new(
        Id: ComValue.ToStringOrEmpty(holiday.GetProperty("MasterHolidayID")),
        Name: ComValue.ToStringOrEmpty(holiday.GetProperty("Name")),
        Year: ComValue.ToInt(holiday.GetProperty("Year")),
        Month: ComValue.ToInt(holiday.GetProperty("Month")),
        Day: ComValue.ToInt(holiday.GetProperty("Day")),
        Type: ComValue.ToInt(holiday.GetProperty("HolidayType")),
        AppliesToAllYears: ComValue.ToBool(holiday.GetProperty("ApplyAllYears")));

    /// <summary>Založí svátek a vrátí jeho id (<c>AddHoliday</c>).</summary>
    public string AddHoliday(UpsertHolidayRequest request)
    {
        EnsureSession();
        var holiday = CreateHolidayObject(request);

        var args = new object?[] { holiday.Target, 0, 0 };
        App.Invoke("AddHoliday", args);
        WinPakStatus.EnsureCardSucceeded("Založení svátku", ComValue.ToInt(args[2]));
        return ComValue.ToStringOrEmpty(args[1]);
    }

    public void EditHoliday(string currentName, UpsertHolidayRequest request)
    {
        EnsureSession();
        var holiday = CreateHolidayObject(request);

        var args = new object?[] { currentName, holiday.Target, 0 };
        App.Invoke("EditHoliday", args);
        WinPakStatus.EnsureCardSucceeded("Úprava svátku", ComValue.ToInt(args[2]));
    }

    private IComDispatch CreateHolidayObject(UpsertHolidayRequest request)
    {
        var holiday = _com.Create(_options.HolidayProgId);
        holiday.SetProperty("Name", request.Name);
        holiday.SetProperty("Year", request.Year);
        holiday.SetProperty("Month", request.Month);
        holiday.SetProperty("Day", request.Day);
        holiday.SetProperty("HolidayType", request.Type);
        holiday.SetProperty("ApplyAllYears", request.AppliesToAllYears);
        return holiday;
    }

    public void DeleteHoliday(string holidayId)
        => CallCardWrite("Smazání svátku", "DeleteHoliday", ComValue.ToLong(holidayId), 0);

    public IReadOnlyList<HolidayGroupDto> GetHolidayGroups()
        => CallList("GetHolidayGroupsByAcctID", MapHolidayGroup, AccountId, null);

    private static HolidayGroupDto MapHolidayGroup(IComDispatch group) => new(
        ComValue.ToStringOrEmpty(group.GetProperty("HolGrpID")),
        ComValue.ToStringOrEmpty(group.GetProperty("HolGrpName")),
        ComValue.ToStringOrNull(group.GetProperty("AccountId")));

    public IReadOnlyList<HolidayDto> GetHolidaysInGroup(string holidayGroupId)
        => CallList("GetHolidaysByHolidayGroupID", MapHoliday, ComValue.ToLong(holidayGroupId), null);

    public void AddHolidayGroup(UpsertHolidayGroupRequest request)
    {
        EnsureSession();
        var group = _com.Create(_options.HolidayGroupProgId);
        group.SetProperty("HolGrpName", request.Name);
        group.SetProperty("AccountId", AccountId);

        var args = new object?[]
        {
            group.Target, ToIds(request.HolidayIds), ToIds(request.MasterHolidayIds), 0,
        };
        App.Invoke("AddHolidayGroup", args);
        WinPakStatus.EnsureCardSucceeded("Založení skupiny svátků", ComValue.ToInt(args[3]));
    }

    public void EditHolidayGroup(string currentName, UpsertHolidayGroupRequest request)
    {
        EnsureSession();
        var group = _com.Create(_options.HolidayGroupProgId);
        group.SetProperty("HolGrpName", request.Name);
        group.SetProperty("AccountId", AccountId);

        var args = new object?[] { currentName, group.Target, ToIds(request.HolidayIds), 0 };
        App.Invoke("EditHolidayGroup", args);
        WinPakStatus.EnsureCardSucceeded("Úprava skupiny svátků", ComValue.ToInt(args[3]));
    }

    public void DeleteHolidayGroup(string holidayGroupId)
        => CallCardWrite("Smazání skupiny svátků", "DeleteHolidayGroup", ComValue.ToLong(holidayGroupId), 0);

    /// <summary>Panely, které skupinu svátků používají — nutné před jejím zrušením.</summary>
    public IReadOnlyList<PanelDto> IsolatePanelsForHolidayGroupDelete(string holidayGroupId)
    {
        var result = Call("IsolatePanelsForHGDelete", AccountId, ComValue.ToLong(holidayGroupId), null, 0);
        WinPakStatus.EnsureCardSucceeded("Vyhledání panelů skupiny svátků", ComValue.ToInt(result[3]));
        return ComValue.AsEnumerable(result[2]).Select(_com.Wrap).Select(MapPanel).ToList();
    }

    public IReadOnlyList<HolidayGroupDto> GetConfiguredHolidayGroupsOfPanel(long panelId)
        => CallList("GetConfiguredHolidayGroupsByPanel", MapHolidayGroup, panelId, null);

    public void ConfigurePanelHolidayGroups(long panelId, IReadOnlyList<string> holidayGroupIds)
        => CallCardWrite("Nastavení skupin svátků panelu", "ConfigurePanelHolidayGroup",
            AccountId, panelId, ToIds(holidayGroupIds), 0);
}
