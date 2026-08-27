using Acs.WinPakConnector.Models;
using Acs.WinPakConnector.Providers.Com;
using Xunit;

namespace Acs.WinPakConnector.Tests;

/// <summary>Mapování zbývajících povelů Communication Server API.</summary>
public sealed class WinPakCommCommandTests
{
    private const string ServerProgId = "ACCW.MTSCBServer";

    private readonly FakeComFactory _com = new();
    private readonly WinPakComOptions _options = new()
    {
        UserName = "acs-service",
        Password = "tajne",
        EnableCommunicationServer = true,
    };

    private FakeComDispatch Server => (FakeComDispatch)_com.Create(ServerProgId);

    private WinPakCommApi CreateApi()
    {
        Server.Returns["InitServer"] = true;
        var api = new WinPakCommApi(_com, _options);
        api.EnsureStarted();
        return api;
    }

    [Fact]
    public void Alarmy_se_potvrzuji_a_rusi_s_HID_a_bodem()
    {
        var api = CreateApi();

        api.AcknowledgeAlarm(23, 5);
        api.ClearAlarm(23, 5);

        Assert.Equal([23L, 5], _com.Call("AckAlarm").Args);
        Assert.Equal([23L, 5], _com.Call("ClrAlarm").Args);
    }

    [Fact]
    public void Poznamka_k_transakci_nese_text()
    {
        CreateApi().AddNote(23, 1, "Prověřeno ostrahou");

        Assert.Equal([23L, 1, "Prověřeno ostrahou"], _com.Call("AddNote").Args);
    }

    [Fact]
    public void Detail_transakce_se_cte_z_out_parametru()
    {
        Server.OutValues["GetDetailsByID#2"] = "Valid card, reader 1";

        var detail = CreateApi().GetTransactionDetails(23, 1);

        Assert.Equal("Valid card, reader 1", detail.Details);
        Assert.Equal("23", detail.Hid);
    }

    [Fact]
    public void Shunt_a_unshunt_pouzivaji_varianty_ByHID()
    {
        var api = CreateApi();

        api.ShuntAlarm(23);
        api.UnshuntAlarm(23);

        Assert.Equal(23L, _com.Call("AlarmShuntByHID").Args[0]);
        Assert.Equal(23L, _com.Call("AlarmUnShuntByHID").Args[0]);
    }

    [Fact]
    public void Odshuntovani_konkretniho_bodu_ma_vlastni_volani()
    {
        CreateApi().UnshuntAlarmPoint(23, 5);

        Assert.Equal([23L, 5], _com.Call("AlarmUnShunt").Args);
    }

    [Fact]
    public void Starsi_ciselny_stav_dveri_se_cte_z_navratove_hodnoty()
    {
        Server.Returns["GetDoorStatus"] = 1;

        Assert.Equal(1, CreateApi().GetDoorStatusCode(23));
    }

    [Fact]
    public void Nezname_ciselne_stavy_dveri_vraci_minus_jedna()
    {
        Assert.Equal(-1, CreateApi().GetDoorStatusCode(23));
    }

    [Fact]
    public void Buffer_a_unbuffer_nesou_rezim()
    {
        var api = CreateApi();

        api.Buffer(23, 1);
        api.Unbuffer(23, 0);

        Assert.Equal([23L, 1], _com.Call("BufferByHID").Args);
        Assert.Equal([23L, 0], _com.Call("UnBufferByHID").Args);
    }

    [Fact]
    public void Vystupy_se_spinaji_pres_Energize_a_DeEnergize()
    {
        var api = CreateApi();

        api.Energize(31);
        api.DeEnergize(31);

        Assert.Equal(31L, _com.Call("Energize").Args[0]);
        Assert.Equal(31L, _com.Call("DeEnergize").Args[0]);
    }

    [Fact]
    public void Inicializace_panelu_posila_pole_priznaku_kroku()
    {
        CreateApi().InitializePanel(12, panelType: 3, tasks: [1, 4]);

        var args = _com.Call("PanelInitialize").Args;
        Assert.Equal(12L, args[0]);
        Assert.Equal(3, args[1]);

        var flags = Assert.IsType<object?[]>(args[2]);
        Assert.Equal(20, flags.Length);          // příručka pracuje s polem 20 kroků
        Assert.Equal(true, flags[1]);
        Assert.Equal(true, flags[4]);
        Assert.Equal(false, flags[0]);
    }

    [Fact]
    public void Hromadne_zamknuti_dveri_nese_ucet_a_priznak()
    {
        CreateApi().LockUnlockAllDoors(8, shouldLock: true);

        Assert.Equal([8L, true], _com.Call("LockUnLockAllDoors").Args);
    }

    [Fact]
    public void Refresh_dveri_vraci_status_z_out_parametru()
    {
        Server.OutValues["RefreshDoorsByAccId#1"] = 7;

        Assert.Equal(7, CreateApi().RefreshDoors(8));
    }

    [Fact]
    public void Door_schedule_posila_pet_parametru_v_poradi_podle_prirucky()
    {
        CreateApi().ExecuteDoorSchedule(new DoorScheduleRequest(
            PanelHid: 12, PanelType: 3, EntranceId: 4, EntrancePointId: 1, TimeZoneId: 2));

        Assert.Equal([12L, 3, 4L, 1L, 2L], _com.Call("ExecuteDoorSchedule").Args);
    }

    [Fact]
    public void Rezim_dveri_NetAXS_se_cte_ze_vraceneho_objektu()
    {
        Server.OutValues["GetNetAXSDoorModeByHID#1"] = new object[]
        {
            _com.Record("mode",
                ("DisableDoorTimezone", 0), ("LockdownReaderTimezone", 0),
                ("CardOnlyTimezone", 2), ("PINOnlyTimezone", 0),
                ("CardOrPINTimezone", 0), ("CardAndPINTimezone", 3),
                ("CardOnlyPriority", 1), ("PINOnlyPriority", 0),
                ("CardOrPINPriority", 0), ("CardAndPINPriority", 2)),
        };

        var mode = CreateApi().GetNetAxsDoorMode(23);

        Assert.Equal(2, mode.CardOnlyTimeZone);
        Assert.Equal(3, mode.CardAndPinTimeZone);
        Assert.Equal(2, mode.CardAndPinPriority);
    }

    [Fact]
    public void Zapis_rezimu_NetAXS_naplni_objekt_a_zkontroluje_status()
    {
        Server.OutValues["SetNetAXSDoorModeByHID#2"] = 0;

        CreateApi().SetNetAxsDoorMode(23, new NetAxsDoorModeDto(0, 0, 2, 0, 0, 3, 1, 0, 0, 2));

        var info = (FakeComDispatch)_com.Create(_options.NetAxsDoorInfoProgId);
        Assert.Equal(2, info.Properties["CardOnlyTimezone"]);
        Assert.Equal(3, info.Properties["CardAndPINTimezone"]);
        Assert.Equal(23L, _com.Call("SetNetAXSDoorModeByHID").Args[0]);
    }

    [Fact]
    public void Odmitnuty_zapis_rezimu_NetAXS_skonci_vyjimkou()
    {
        Server.OutValues["SetNetAXSDoorModeByHID#2"] = 1;

        Assert.Throws<WinPakOperationException>(
            () => CreateApi().SetNetAxsDoorMode(23, new NetAxsDoorModeDto(0, 0, 0, 0, 0, 0, 0, 0, 0, 0)));
    }

    [Fact]
    public void Stav_zarizeni_se_cte_z_navratove_hodnoty()
    {
        Server.Returns["GetStatus"] = 5;

        Assert.Equal(5, CreateApi().GetDeviceStatus(23, deviceType: 2));
        Assert.Equal([23L, 2, 0], _com.Call("GetStatus").Args);
    }

    [Fact]
    public void Vychozi_rezim_ctecky_se_cte_z_out_parametru()
    {
        Server.OutValues["GetDefaultACRMode#1"] = 5;

        Assert.Equal(5, CreateApi().GetDefaultReaderMode(23));
    }

    [Fact]
    public void Filtry_udalosti_se_pridavaji_a_ctou_jako_seznam_id()
    {
        Server.OutValues["GetFilterHIDs#0"] = "23, 24;25";
        var api = CreateApi();

        api.AddEventFilter(23);
        api.RemoveEventFilter(24);

        Assert.Equal(23L, _com.Call("AddFilterHID").Args[0]);
        Assert.Equal(24L, _com.Call("RemoveFilterHID").Args[0]);
        Assert.Equal(["23", "24", "25"], api.GetEventFilters());
    }

    [Fact]
    public void Filtry_komunikacnich_serveru_maji_vlastni_volani()
    {
        Server.OutValues["GetFilterCommServerIDs#0"] = "1";
        var api = CreateApi();

        api.AddCommServerFilter(1);

        Assert.Equal(1L, _com.Call("AddFilterCommServerID").Args[0]);
        Assert.Equal(["1"], api.GetCommServerFilters());
    }

    [Fact]
    public void Muster_vraci_data_jen_pri_uspesnem_statusu()
    {
        Server.OutValues["GetMusterElemenets#0"] = "<Muster/>";
        Server.OutValues["GetMusterElemenets#5"] = true;

        var muster = CreateApi().GetMusterElements(areaId: 2, accountId: 8, sortField: 1, sortOrder: 0);

        Assert.Equal("<Muster/>", muster.Raw);
        var args = _com.Call("GetMusterElemenets").Args;
        Assert.Equal(2L, args[1]);
        Assert.Equal(8L, args[2]);
    }

    [Fact]
    public void Neuspesny_muster_skonci_chybou()
    {
        Server.OutValues["GetMusterElemenets#5"] = false;

        Assert.Throws<InvalidOperationException>(() => CreateApi().GetMusterElements(2, 8, 0, 0));
    }

    [Fact]
    public void Vlastni_prikaz_se_posila_i_s_HID()
    {
        CreateApi().ExecuteCustomCommand(23, "REBOOT");

        Assert.Equal([23L, "REBOOT"], _com.Call("ExecCustomCommand").Args);
    }

    [Fact]
    public void Domeny_lze_zjistit_jeste_pred_registraci_u_serveru()
    {
        Server.OutValues["GetConfiguredWPDomains#0"] = new object[] { "FNMH" };

        var api = new WinPakCommApi(_com, _options);

        Assert.Equal(["FNMH"], api.GetConfiguredDomains());
        Assert.Empty(_com.AllCalls("InitServer"));
    }
}
