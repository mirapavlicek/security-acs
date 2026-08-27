using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers.Com;

/// <summary>
/// Zbývající povely Communication Server API: alarmy, výstupy, panely,
/// filtry událostí, muster a vlastní příkazy.
/// </summary>
public sealed partial class WinPakCommApi
{
    // ---------- Alarmy a transakce ----------

    public void AcknowledgeAlarm(long hid, int point)
    {
        EnsureStarted();
        Server.Invoke("AckAlarm", [hid, point]);
    }

    public void ClearAlarm(long hid, int point)
    {
        EnsureStarted();
        Server.Invoke("ClrAlarm", [hid, point]);
    }

    public void AddNote(long hid, int point, string note)
    {
        EnsureStarted();
        Server.Invoke("AddNote", [hid, point, note]);
    }

    public TransactionDetailDto GetTransactionDetails(long hid, int point)
    {
        EnsureStarted();
        var args = new object?[] { hid, point, null };
        Server.Invoke("GetDetailsByID", args);
        return new TransactionDetailDto(hid.ToString(), point, ComValue.ToStringOrNull(args[2]));
    }

    public void ShuntAlarm(long hid)
    {
        EnsureStarted();
        Server.Invoke("AlarmShuntByHID", [hid]);
    }

    public void UnshuntAlarm(long hid)
    {
        EnsureStarted();
        Server.Invoke("AlarmUnShuntByHID", [hid]);
    }

    /// <summary>Odshuntování konkrétního bodu zařízení (<c>AlarmUnShunt</c>).</summary>
    public void UnshuntAlarmPoint(long hid, int point)
    {
        EnsureStarted();
        Server.Invoke("AlarmUnShunt", [hid, point]);
    }

    /// <summary>
    /// Starší číselná varianta stavu dveří (<c>GetDoorStatus</c>):
    /// 0 zavřeno, 1 otevřeno, -1 neznámo.
    /// </summary>
    public int GetDoorStatusCode(long hid)
    {
        EnsureStarted();
        return ComValue.ToInt(Server.Invoke("GetDoorStatus", [hid]), -1);
    }

    // ---------- Vstupní body podle bodu ----------

    /// <summary>Zamkne konkrétní bod zařízení (varianta <c>EntryPointLock</c> s číslem bodu).</summary>
    public void LockEntryPoint(long hid, int point)
    {
        EnsureStarted();
        Server.Invoke("EntryPointLock", [hid, point]);
    }

    public void UnlockEntryPoint(long hid, int point)
    {
        EnsureStarted();
        Server.Invoke("EntryPointUnLock", [hid, point]);
    }

    /// <summary>Vrátí zařízení pod kontrolu časové zóny (<c>RestoreTZByHID</c>).</summary>
    public void RestoreTimeZone(long hid)
    {
        EnsureStarted();
        Server.Invoke("RestoreTZByHID", [hid]);
    }

    // ---------- Buffer transakcí ----------

    /// <summary>Zapne bufferování transakcí zařízení (0 = hard, 1 = soft).</summary>
    public void Buffer(long hid, int mode)
    {
        EnsureStarted();
        Server.Invoke("BufferByHID", [hid, mode]);
    }

    public void Unbuffer(long hid, int mode)
    {
        EnsureStarted();
        Server.Invoke("UnBufferByHID", [hid, mode]);
    }

    // ---------- Výstupy ----------

    public void Energize(long hid)
    {
        EnsureStarted();
        Server.Invoke("Energize", [hid]);
    }

    public void DeEnergize(long hid)
    {
        EnsureStarted();
        Server.Invoke("DeEnergize", [hid]);
    }

    // ---------- Panely ----------

    /// <summary>Spustí inicializaci panelu; <c>tasks</c> jsou indexy kroků podle <c>panelInitTask</c>.</summary>
    public void InitializePanel(long hid, int panelType, IReadOnlyList<int> tasks)
    {
        EnsureStarted();

        // Příručka posílá pole příznaků, kde index odpovídá kroku inicializace.
        var flags = new object?[Math.Max(tasks.Count == 0 ? 0 : tasks.Max() + 1, 20)];
        for (var i = 0; i < flags.Length; i++)
            flags[i] = false;
        foreach (var task in tasks.Where(t => t >= 0 && t < flags.Length))
            flags[task] = true;

        Server.Invoke("PanelInitialize", [hid, panelType, flags]);
    }

    public void CancelPanelInitialize(long hid)
    {
        EnsureStarted();
        Server.Invoke("PanelCancelInitialize", [hid]);
    }

    /// <summary>Znovu nahraje časové zóny do panelu (<c>PanelRefreshTZByHID</c>).</summary>
    public void RefreshPanelTimeZones(long hid)
    {
        EnsureStarted();
        Server.Invoke("PanelRefreshTZByHID", [hid]);
    }

    // ---------- Dveře hromadně ----------

    public void LockUnlockAllDoors(long accountId, bool shouldLock)
    {
        EnsureStarted();
        Server.Invoke("LockUnLockAllDoors", [accountId, shouldLock]);
    }

    /// <summary>Obnoví stav dveří účtu a vrátí návratový status (<c>RefreshDoorsByAccId</c>).</summary>
    public int RefreshDoors(long accountId)
    {
        EnsureStarted();
        var args = new object?[] { accountId, 0 };
        Server.Invoke("RefreshDoorsByAccId", args);
        return ComValue.ToInt(args[1]);
    }

    /// <summary>Spustí door schedule na vstupu panelu (<c>ExecuteDoorSchedule</c>).</summary>
    public void ExecuteDoorSchedule(DoorScheduleRequest request)
    {
        EnsureStarted();
        Server.Invoke("ExecuteDoorSchedule",
            [request.PanelHid, request.PanelType, request.EntranceId, request.EntrancePointId, request.TimeZoneId]);
    }

    // ---------- NetAXS ----------

    public NetAxsDoorModeDto GetNetAxsDoorMode(long hid)
    {
        EnsureStarted();
        var args = new object?[] { hid, null };
        Server.Invoke("GetNetAXSDoorModeByHID", args);

        var raw = ComValue.AsEnumerable(args[1]).FirstOrDefault()
            ?? throw new KeyNotFoundException($"WIN-PAK nevrátil režim dveří pro zařízení {hid}.");
        var info = _com.Wrap(raw);

        return new NetAxsDoorModeDto(
            ComValue.ToInt(info.GetProperty("DisableDoorTimezone")),
            ComValue.ToInt(info.GetProperty("LockdownReaderTimezone")),
            ComValue.ToInt(info.GetProperty("CardOnlyTimezone")),
            ComValue.ToInt(info.GetProperty("PINOnlyTimezone")),
            ComValue.ToInt(info.GetProperty("CardOrPINTimezone")),
            ComValue.ToInt(info.GetProperty("CardAndPINTimezone")),
            ComValue.ToInt(info.GetProperty("CardOnlyPriority")),
            ComValue.ToInt(info.GetProperty("PINOnlyPriority")),
            ComValue.ToInt(info.GetProperty("CardOrPINPriority")),
            ComValue.ToInt(info.GetProperty("CardAndPINPriority")));
    }

    public void SetNetAxsDoorMode(long hid, NetAxsDoorModeDto mode)
    {
        EnsureStarted();
        var info = _com.Create(_options.NetAxsDoorInfoProgId);
        info.SetProperty("DisableDoorTimezone", mode.DisableDoorTimeZone);
        info.SetProperty("LockdownReaderTimezone", mode.LockdownReaderTimeZone);
        info.SetProperty("CardOnlyTimezone", mode.CardOnlyTimeZone);
        info.SetProperty("PINOnlyTimezone", mode.PinOnlyTimeZone);
        info.SetProperty("CardOrPINTimezone", mode.CardOrPinTimeZone);
        info.SetProperty("CardAndPINTimezone", mode.CardAndPinTimeZone);
        info.SetProperty("CardOnlyPriority", mode.CardOnlyPriority);
        info.SetProperty("PINOnlyPriority", mode.PinOnlyPriority);
        info.SetProperty("CardOrPINPriority", mode.CardOrPinPriority);
        info.SetProperty("CardAndPINPriority", mode.CardAndPinPriority);

        var args = new object?[] { hid, info.Target, 0 };
        Server.Invoke("SetNetAXSDoorModeByHID", args);
        WinPakStatus.EnsureCardSucceeded("Nastavení režimu dveří NetAXS", ComValue.ToInt(args[2]));
    }

    // ---------- Stav a filtry ----------

    /// <summary>Stavové id zařízení podle jeho typu (<c>GetStatus</c>).</summary>
    public int GetDeviceStatus(long hid, int deviceType)
    {
        EnsureStarted();
        return ComValue.ToInt(Server.Invoke("GetStatus", [hid, deviceType, 0]));
    }

    /// <summary>Výchozí režim čtečky (<c>GetDefaultACRMode</c>).</summary>
    public int GetDefaultReaderMode(long hid)
    {
        EnsureStarted();
        var args = new object?[] { hid, 0 };
        Server.Invoke("GetDefaultACRMode", args);
        return ComValue.ToInt(args[1]);
    }

    /// <summary>Omezí odebírané události na vyjmenovaná zařízení.</summary>
    public void AddEventFilter(long hid)
    {
        EnsureStarted();
        Server.Invoke("AddFilterHID", [hid]);
    }

    public void RemoveEventFilter(long hid)
    {
        EnsureStarted();
        Server.Invoke("RemoveFilterHID", [hid]);
    }

    public IReadOnlyList<string> GetEventFilters()
    {
        EnsureStarted();
        var args = new object?[] { null };
        Server.Invoke("GetFilterHIDs", args);
        return SplitIds(ComValue.ToStringOrNull(args[0]));
    }

    public void AddCommServerFilter(long commServerId)
    {
        EnsureStarted();
        Server.Invoke("AddFilterCommServerID", [commServerId]);
    }

    public void RemoveCommServerFilter(long commServerId)
    {
        EnsureStarted();
        Server.Invoke("RemoveFilterCommServerID", [commServerId]);
    }

    public IReadOnlyList<string> GetCommServerFilters()
    {
        EnsureStarted();
        var args = new object?[] { null };
        Server.Invoke("GetFilterCommServerIDs", args);
        return SplitIds(ComValue.ToStringOrNull(args[0]));
    }

    /// <summary>Filtry se vracejí jako jeden řetězec oddělený čárkami nebo středníky.</summary>
    private static IReadOnlyList<string> SplitIds(string? value)
        => value is null
            ? []
            : value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ---------- Muster a vlastní příkazy ----------

    /// <summary>Muster report pro danou oblast (kdo se kde nachází).</summary>
    public MusterElementDto GetMusterElements(long areaId, long accountId, int sortField, int sortOrder)
    {
        EnsureStarted();
        var args = new object?[] { null, areaId, accountId, sortField, sortOrder, false };
        Server.Invoke("GetMusterElemenets", args);

        if (!ComValue.ToBool(args[5]))
            throw new InvalidOperationException("WIN-PAK muster report nevrátil data.");

        return new MusterElementDto(ComValue.ToStringOrNull(args[0]));
    }

    /// <summary>Vlastní příkaz pro zařízení (<c>ExecCustomCommand</c>).</summary>
    public void ExecuteCustomCommand(long hid, string command)
    {
        EnsureStarted();
        Server.Invoke("ExecCustomCommand", [hid, command]);
    }

    /// <summary>Domény nakonfigurované ve WIN-PAK; jediné volání použitelné před <c>InitServer</c>.</summary>
    public IReadOnlyList<string> GetConfiguredDomains()
    {
        var server = _server ?? _com.Create(_options.CommServerProgId);
        var args = new object?[] { null };
        server.Invoke("GetConfiguredWPDomains", args);

        return ComValue.AsEnumerable(args[0])
            .Select(ComValue.ToStringOrEmpty)
            .Where(domain => domain.Length > 0)
            .ToList();
    }
}
