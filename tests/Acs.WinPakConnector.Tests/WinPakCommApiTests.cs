using Acs.WinPakConnector.Models;
using Acs.WinPakConnector.Providers.Com;
using Xunit;

namespace Acs.WinPakConnector.Tests;

/// <summary>Mapování povelů pro dveře na Communication Server API.</summary>
public sealed class WinPakCommApiTests
{
    private const string ServerProgId = "ACCW.MTSCBServer";

    private readonly FakeComFactory _com = new();
    private readonly WinPakComOptions _options = new()
    {
        UserName = "acs-service",
        Password = "tajne",
        CommViewType = 4,
        EnableCommunicationServer = true,
    };

    private FakeComDispatch Server => (FakeComDispatch)_com.Create(ServerProgId);

    private WinPakCommApi CreateApi(bool started = true)
    {
        Server.Returns["InitServer"] = true;
        Server.Returns["InitServer2"] = true;
        var api = new WinPakCommApi(_com, _options);
        if (started)
            api.EnsureStarted();
        return api;
    }

    [Fact]
    public void Bez_domeny_se_registruje_pres_InitServer()
    {
        CreateApi();

        var call = _com.Call("InitServer");
        Assert.Equal(4, call.Args[1]);                     // enumViewType
        Assert.Equal(_options.UserName, call.Args[2]);
        Assert.Equal(_options.Password, call.Args[3]);
        Assert.IsType<WinPakCallbackSink>(call.Args[0]);   // Caller = náš callback
    }

    [Fact]
    public void S_domenou_se_registruje_pres_InitServer2()
    {
        _options.Domain = "FNMH";

        CreateApi();

        var call = _com.Call("InitServer2");
        Assert.Equal("FNMH", call.Args[4]);
        Assert.Empty(_com.AllCalls("InitServer"));
    }

    [Fact]
    public void Odmitnuta_registrace_skonci_chybou()
    {
        Server.Returns["InitServer"] = false;

        var api = new WinPakCommApi(_com, _options);

        Assert.Throws<InvalidOperationException>(api.EnsureStarted);
        Assert.False(api.IsStarted);
    }

    [Fact]
    public void Zamknuti_a_odemknuti_pouziva_EntryPoint_volani_s_HID()
    {
        var api = CreateApi();

        api.LockDoor(23);
        api.UnlockDoor(23);

        Assert.Equal(23L, _com.Call("EntryPointLockByID").Args[0]);
        Assert.Equal(23L, _com.Call("EntryPointUnLockByID").Args[0]);
    }

    [Fact]
    public void Kdyz_server_nema_variantu_podle_id_zamkne_se_bod_nula()
    {
        // Komunikační server FN Motol EntryPointLockByID nemá; má EntryPointLock(hid, point).
        Server.Throws["EntryPointLockByID"] = new ComCallException("EntryPointLockByID",
            new System.Runtime.InteropServices.COMException("Unknown name.", unchecked((int)0x80020006)));
        var api = CreateApi();

        api.LockDoor(23);
        api.LockDoor(24);

        Assert.Equal([23L, 0], _com.Calls.First(c => c.Method == "EntryPointLock").Args);
        // Chybějící metoda se pamatuje — druhé zamknutí už ByID nezkouší.
        Assert.Equal(1, _com.Calls.Count(c => c.Method == "EntryPointLockByID"));
        Assert.Equal(2, _com.Calls.Count(c => c.Method == "EntryPointLock"));
    }

    [Fact]
    public void Puls_bez_delky_pouzije_PulseByHID()
    {
        CreateApi().Pulse(23, seconds: null);

        Assert.Equal(23L, _com.Call("PulseByHID").Args[0]);
        Assert.Empty(_com.AllCalls("TimedPulseByHID"));
    }

    [Fact]
    public void Puls_s_delkou_pouzije_TimedPulseByHID_v_sekundach()
    {
        CreateApi().Pulse(23, seconds: 5);

        // Jednotka 0 = sekundy podle příručky.
        Assert.Equal([23L, 0, 5], _com.Call("TimedPulseByHID").Args);
    }

    [Fact]
    public void Rezim_dveri_se_posila_jako_cislo_podle_prirucky()
    {
        CreateApi().SetDoorMode(23, DoorMode.CardAndPin);

        Assert.Equal([23L, 7], _com.Call("DoorModeByHID").Args);
    }

    [Fact]
    public void Stav_dveri_se_precte_z_odpovedi_GetDoorStatus2()
    {
        Server.Returns["GetDoorStatus2"] =
            "<NLZ><Door_IsOpen>1</Door_IsOpen><ADV_Hid>23</ADV_Hid><ADV_DeviceName>Vchod</ADV_DeviceName></NLZ>";

        var status = CreateApi().GetDoorStatus(23);

        Assert.Equal(23L, _com.Call("GetDoorStatus2").Args[0]);
        Assert.True(status.IsOpen);
        Assert.Equal("Vchod", status.DeviceName);
    }

    [Fact]
    public void Stav_serveru_se_pta_na_vsechny_servery()
    {
        Server.Returns["IsConnected2"] =
            "<NLZ><SrvId>1</SrvId><Server>WPDB</Server><Connected>1</Connected><SerType>1</SerType></NLZ>";

        var servers = CreateApi().GetServerStatus();

        Assert.Equal(0, _com.Call("IsConnected2").Args[0]);   // 0 = všechny servery
        Assert.True(Assert.Single(servers).Connected);
    }
}
