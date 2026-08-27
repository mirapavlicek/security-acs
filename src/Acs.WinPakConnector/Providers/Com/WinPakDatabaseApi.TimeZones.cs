using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers.Com;

/// <summary>Časové zóny, jejich intervaly a přiřazení panelům.</summary>
public sealed partial class WinPakDatabaseApi
{
    public IReadOnlyList<TimeZoneDto> GetTimeZones()
        => string.IsNullOrWhiteSpace(_options.AccountName)
            ? CallList("GetAllTimezones", MapTimeZone, [null])
            : CallList("GetTimeZonesByAccountName", MapTimeZone, _options.AccountName, null);

    private static TimeZoneDto MapTimeZone(IComDispatch zone) => new(
        ComValue.ToStringOrEmpty(zone.GetProperty("TimeZoneID")),
        ComValue.ToStringOrEmpty(zone.GetProperty("TimeZoneName")),
        ComValue.ToStringOrNull(zone.GetProperty("TimeZoneDesc")),
        ComValue.ToStringOrNull(zone.GetProperty("AccountName")));

    public TimeZoneDto? GetTimeZoneByName(string name)
    {
        var result = Call("GetTimeZoneByName", name, null);
        var raw = ComValue.AsEnumerable(result[1]).FirstOrDefault();
        return raw is null ? null : MapTimeZone(_com.Wrap(raw));
    }

    public string? GetTimeZoneName(string timeZoneId)
        => ComValue.ToStringOrNull(Call("GetTimezoneNameByID", ComValue.ToLong(timeZoneId), null)[1]);

    /// <summary>Založí časovou zónu přes objekt <c>NCIHelper.TimeZone</c> a vrátí přidělené id.</summary>
    public string AddTimeZone(UpsertTimeZoneRequest request)
    {
        EnsureSession();
        var zone = _com.Create(_options.TimeZoneProgId);
        zone.SetProperty("TimeZoneName", request.Name);
        zone.SetProperty("TimeZoneDesc", request.Description ?? "");
        zone.SetProperty("AccountName", _options.AccountName);

        var args = new object?[] { zone.Target, 0, 0 };
        App.Invoke("AddTimezone", args);
        WinPakStatus.EnsureCardSucceeded("Založení časové zóny", ComValue.ToInt(args[2]));
        return ComValue.ToStringOrEmpty(args[1]);
    }

    /// <summary>Jednoduché založení zóny pro více účtů (<c>CreateTimezone</c>).</summary>
    public void CreateTimeZone(UpsertTimeZoneRequest request)
    {
        var accountIds = request.AccountIds is { Count: > 0 } ? ToIds(request.AccountIds) : [AccountId];
        CallCardWrite("Založení časové zóny", "CreateTimezone",
            request.Name, request.Description ?? "", accountIds, 0);
    }

    public void EditTimeZone(string currentName, UpsertTimeZoneRequest request)
    {
        EnsureSession();
        var zone = _com.Create(_options.TimeZoneProgId);
        zone.SetProperty("TimeZoneName", request.Name);
        zone.SetProperty("TimeZoneDesc", request.Description ?? "");
        zone.SetProperty("AccountName", _options.AccountName);

        var args = new object?[] { currentName, _options.AccountName, zone.Target, 0 };
        App.Invoke("EditTimeZone", args);
        WinPakStatus.EnsureCardSucceeded("Úprava časové zóny", ComValue.ToInt(args[3]));
    }

    public void DeleteTimeZone(string timeZoneId)
        => CallCardWrite("Smazání časové zóny", "DeleteTimeZone", ComValue.ToLong(timeZoneId), 0);

    public IReadOnlyList<TimeZoneRangeDto> GetTimeZoneRanges(string timeZoneId)
        => CallList("GetTimeZoneRangesByTZID",
            range => new TimeZoneRangeDto(
                ComValue.ToStringOrEmpty(range.GetProperty("TZRangeID")),
                timeZoneId,
                ComValue.ToStringOrNull(range.GetProperty("StartTime")),
                ComValue.ToStringOrNull(range.GetProperty("EndTime")),
                ComValue.ToInt(range.GetProperty("DayType"))),
            ComValue.ToLong(timeZoneId), null);

    /// <summary>Nastaví intervaly časové zóny (<c>ConfigureTimeZoneRange</c>).</summary>
    public void ConfigureTimeZoneRanges(string timeZoneId, IReadOnlyList<TimeZoneRangeRequest> ranges)
    {
        // Příručka posílá intervaly jako VARIANT pole trojic den/od/do.
        var payload = ranges
            .SelectMany(r => new object?[] { r.DayType, r.StartTime, r.EndTime })
            .ToArray();

        CallCardWrite("Nastavení intervalů časové zóny", "ConfigureTimeZoneRange",
            AccountId, ComValue.ToLong(timeZoneId), payload, 0);
    }

    public void DeleteTimeZoneRange(string timeZoneId, string rangeId)
        => CallCardWrite("Smazání intervalu časové zóny", "DeleteTimeZoneRange",
            ComValue.ToLong(timeZoneId), ComValue.ToLong(rangeId), 0);

    /// <summary>Časové zóny dostupné pro panel a ty, které už na něm jsou nakonfigurované.</summary>
    public IReadOnlyList<TimeZoneDto> GetAvailableTimeZonesOfPanel(long panelId)
        => CallList("GetAvailableTimezonesOfPanel", MapTimeZone, AccountId, panelId, null);

    public IReadOnlyList<TimeZoneDto> GetConfiguredTimeZonesOfPanel(long panelId)
        => CallList("GetConfiguredTimezonesByPanel", MapTimeZone, panelId, null);

    public void ConfigurePanelTimeZones(long panelId, IReadOnlyList<string> timeZoneIds)
        => CallCardWrite("Nastavení časových zón panelu", "ConfigurePanelTimeZone",
            AccountId, panelId, ToIds(timeZoneIds), 0);

    /// <summary>Časové zóny dostupné pro čtečku (<c>GetAvailableTimeZonesOfReader</c>).</summary>
    public IReadOnlyList<TimeZoneDto> GetAvailableTimeZonesOfReader(string readerName)
        => CallList("GetAvailableTimeZonesOfReader", MapTimeZone, readerName, null);

    /// <summary>Časové zóny dostupné pro čtečku v rámci účtu (<c>GetAvailableTimeZonesOfAccessReader</c>).</summary>
    public IReadOnlyList<TimeZoneDto> GetAvailableTimeZonesOfAccessReader(string readerName)
        => CallList("GetAvailableTimeZonesOfAccessReader", MapTimeZone, _options.AccountName, readerName, null);

    /// <summary>Časová zóna, kterou má čtečka v dané přístupové úrovni.</summary>
    public TimeZoneDto? GetAssociatedTimeZoneOfReader(string accessLevelName, string readerName)
    {
        var result = Call("GetAssociatedTimeZoneOfReader",
            accessLevelName, _options.AccountName, readerName, null);
        var raw = ComValue.AsEnumerable(result[3]).FirstOrDefault();
        return raw is null ? null : MapTimeZone(_com.Wrap(raw));
    }

    /// <summary>Souhrn časových zón čteček účtu, jak ho vrací <c>GetReaderTZDetailsByAccountId</c>.</summary>
    public string? GetReaderTimeZoneDetails()
        => ComValue.ToStringOrNull(Call("GetReaderTZDetailsByAccountId", AccountId, null)[1]);

    /// <summary>Přímý bod a časová zóna čtečky (<c>GetDirectPointTZDetailsofReader</c>).</summary>
    public (string? DeviceId, string? TimeZoneId) GetDirectPointTimeZoneOfReader(long readerId)
    {
        var result = Call("GetDirectPointTZDetailsofReader", readerId, 0, 0);
        return (ComValue.ToStringOrNull(result[1]), ComValue.ToStringOrNull(result[2]));
    }
}
