using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers.Com;

/// <summary>Systémové údaje serveru, operátor, plány reportů a šablony.</summary>
public sealed partial class WinPakDatabaseApi
{
    public (int Id, string Name) GetCurrentOperator()
    {
        var result = Call("GetCurrentOperator", 0, null);
        return (ComValue.ToInt(result[0]), ComValue.ToStringOrEmpty(result[1]));
    }

    /// <summary>Název ODBC zdroje, přes který WIN-PAK běží.</summary>
    public string? GetDataSourceName() => ComValue.ToStringOrNull(Call("GetWPDSN", [null])[0]);

    /// <summary>Časová zóna databázového serveru a zda má zapnutý letní čas.</summary>
    public (string? Name, bool DaylightSaving) GetServerTimeZone()
    {
        var result = Call("GetWPDBServerTZ", [null, 0]);
        return (ComValue.ToStringOrNull(result[0]), ComValue.ToInt(result[1]) != 0);
    }

    public int GetServerTimeZoneOffset() => ComValue.ToInt(Call("GetWPDBServerTZoffset", 0)[0]);

    public IReadOnlyList<string> GetConfiguredDomains()
        => ComValue.AsEnumerable(Call("GetConfiguredWPDomains", [null])[0])
            .Select(ComValue.ToStringOrEmpty)
            .Where(domain => domain.Length > 0)
            .ToList();

    /// <summary>E-mailové adresy účtu pro rozesílání reportů.</summary>
    public string? GetAccountEmails() => ComValue.ToStringOrNull(Call("GetAccountEmailIDs", AccountId, null)[1]);

    /// <summary>Souhrn všeho, co se o instalaci dá zjistit jedním voláním REST.</summary>
    public SystemInfoDto GetSystemInfo()
    {
        var (timeZone, daylightSaving) = GetServerTimeZone();
        var (operatorId, operatorName) = GetCurrentOperator();

        return new SystemInfoDto(
            DataSourceName: GetDataSourceName(),
            ServerTimeZone: timeZone,
            DaylightSavingEnabled: daylightSaving,
            ServerTimeZoneOffsetMinutes: GetServerTimeZoneOffset(),
            MaxCardNumberLength: GetMaxCardNumberLength(),
            CardNumbersAreNumeric: GetCardNumeric(),
            AccessLevelType: GetAccessLevelType(),
            CurrentOperator: operatorId > 0 ? new OperatorDto(operatorId, operatorName) : null,
            Domains: GetConfiguredDomains());
    }

    // ---------- Plány reportů a šablony ----------

    public ScheduleDto? GetSchedule(string scheduleId)
    {
        var result = Call("GetSchedule", ComValue.ToLong(scheduleId), null);
        var raw = ComValue.AsEnumerable(result[1]).FirstOrDefault();
        return raw is null ? null : MapSchedule(_com.Wrap(raw));
    }

    private static ScheduleDto MapSchedule(IComDispatch schedule) => new(
        Id: ComValue.ToStringOrEmpty(schedule.GetProperty("ScheduleId")),
        Name: ComValue.ToStringOrEmpty(schedule.GetProperty("ScheduleName")),
        AccountId: ComValue.ToStringOrNull(schedule.GetProperty("AccountId")),
        ScheduleType: ComValue.ToInt(schedule.GetProperty("ScheduleType")),
        Frequency: ComValue.ToInt(schedule.GetProperty("ScheduleFrequency")),
        ReportType: ComValue.ToInt(schedule.GetProperty("ScheduleReportType")),
        Print: ComValue.ToBool(schedule.GetProperty("SchedulePrintReport")),
        Email: ComValue.ToBool(schedule.GetProperty("ScheduleEmailReport")),
        Fax: ComValue.ToBool(schedule.GetProperty("ScheduleFaxReport")));

    /// <summary>Založí nebo upraví plán reportu (<c>AddEditSchedule</c>).</summary>
    public void UpsertSchedule(ScheduleDto schedule)
    {
        EnsureSession();
        var com = _com.Create(_options.ScheduleProgId);
        com.SetProperty("ScheduleId", ComValue.ToLong(schedule.Id));
        com.SetProperty("ScheduleName", schedule.Name);
        com.SetProperty("AccountId", ComValue.ToLong(schedule.AccountId) is var id && id > 0 ? id : AccountId);
        com.SetProperty("ScheduleType", schedule.ScheduleType);
        com.SetProperty("ScheduleFrequency", schedule.Frequency);
        com.SetProperty("ScheduleReportType", schedule.ReportType);
        com.SetProperty("SchedulePrintReport", schedule.Print);
        com.SetProperty("ScheduleEmailReport", schedule.Email);
        com.SetProperty("ScheduleFaxReport", schedule.Fax);

        var args = new object?[] { com.Target, 0 };
        App.Invoke("AddEditSchedule", args);
        WinPakStatus.EnsureCardSucceeded("Uložení plánu reportu", ComValue.ToInt(args[1]));
    }

    /// <summary>Založí nebo upraví šablonu reportu (<c>AddEditTemplate</c>).</summary>
    public void UpsertTemplate(TemplateDto template)
    {
        EnsureSession();
        var com = _com.Create(_options.TemplateProgId);
        com.SetProperty("TemplateId", ComValue.ToLong(template.Id));
        com.SetProperty("ScheduleName", template.Name);
        com.SetProperty("AccountId", ComValue.ToLong(template.AccountId) is var id && id > 0 ? id : AccountId);
        com.SetProperty("ReportType", template.Type);
        com.SetProperty("TemplateString", template.Definition ?? "");

        var args = new object?[] { com.Target, 0 };
        App.Invoke("AddEditTemplate", args);
        WinPakStatus.EnsureCardSucceeded("Uložení šablony reportu", ComValue.ToInt(args[1]));
    }

    public void DeleteSchedule(string scheduleId)
        => CallCardWrite("Smazání plánu reportu", "DeleteSchedule",
            ComValue.ToLong(scheduleId), AccountId, 0);

    public TemplateDto? GetTemplate(string templateId)
    {
        var result = Call("GetTemplate", ComValue.ToLong(templateId), null);
        var raw = ComValue.AsEnumerable(result[1]).FirstOrDefault();
        if (raw is null)
            return null;

        var template = _com.Wrap(raw);
        return new TemplateDto(
            Id: ComValue.ToStringOrEmpty(template.GetProperty("TemplateId")),
            Name: ComValue.ToStringOrEmpty(template.GetProperty("ScheduleName")),
            AccountId: ComValue.ToStringOrNull(template.GetProperty("AccountId")),
            Type: ComValue.ToInt(template.GetProperty("ReportType")),
            Definition: ComValue.ToStringOrNull(template.GetProperty("TemplateString")));
    }

    public void DeleteTemplate(string templateId)
    {
        var (accountId, subAccountId) = ResolveAccountIds();
        CallCardWrite("Smazání šablony reportu", "DeleteTemplate",
            ComValue.ToLong(templateId), accountId, subAccountId, 0);
    }

    /// <summary>Data a rozměry odznaku (badge) pro tisk karet.</summary>
    public BadgeDto GetBadge(string badgeId)
    {
        var id = ComValue.ToLong(badgeId);
        var data = ComValue.ToStringOrNull(Call("GetBadgeData", id, null)[1]);
        var dimensions = Call("GetBadgeDimension", id, 0, 0);

        return new BadgeDto(badgeId, data,
            ComValue.ToInt(dimensions[1]), ComValue.ToInt(dimensions[2]));
    }
}
