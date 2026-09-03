using System.Reflection;
using System.Runtime.InteropServices;
using Acs.WinPakConnector.Providers.Com;
using Xunit;

namespace Acs.WinPakConnector.Tests;

/// <summary>
/// Poučení z prvního zprovoznění proti skutečnému WIN-PAKu: s nevyplněným účtem
/// vrátil WIN-PAK „0 čteček, 0 držitelů“, a každá chyba COM se ukázala jako
/// „Exception has been thrown by the target of an invocation“.
/// </summary>
public sealed class WinPakAccountResolutionTests
{
    private const string AppProgId = "NCIHelper.Application";
    private readonly FakeComFactory _com = new();

    private FakeComDispatch App => (FakeComDispatch)_com.Create(AppProgId);

    private WinPakDatabaseApi CreateApi(string? accountName) => new(_com, new WinPakComOptions
    {
        UserName = "acs-service", Password = "tajne", Domain = "FNMH",
        AccountName = accountName ?? "", SubAccountName = "",
    });

    private void ArrangeLogin(params string[] accountNames)
    {
        App.OutValues["Login#3"] = 42;
        App.OutValues["ConnectWPDatabase#3"] = 0;
        App.OutValues["GetAccounts#0"] = accountNames
            .Select((name, i) => (object)_com.Record($"acc{i}", ("AccountID", (long)(i + 1)), ("AccountName", name)))
            .ToArray();
        App.OutValues["GetSubAccountsByAccountID#1"] = Array.Empty<object>();
    }

    // ---------- Účet ----------

    [Fact]
    public void Jediny_ucet_ve_WinPaku_se_pouzije_i_kdyz_v_konfiguraci_chybi()
    {
        ArrangeLogin("FN Motol");
        var api = CreateApi(accountName: null);

        api.GetReaders();

        // Čtečky se dotáhly za skutečný účet, ne za prázdný řetězec.
        var call = _com.Call("GetReadersByAccountName");
        Assert.Equal("FN Motol", call.Args[0]);
        Assert.True(api.AccountNameResolvedAutomatically);
    }

    [Fact]
    public void Ucet_z_konfigurace_ma_prednost()
    {
        ArrangeLogin("FN Motol", "Jiny");
        var api = CreateApi(accountName: "Jiny");

        api.GetReaders();

        Assert.Equal("Jiny", _com.Call("GetReadersByAccountName").Args[0]);
        Assert.False(api.AccountNameResolvedAutomatically);
    }

    [Fact]
    public void Vice_uctu_bez_vyberu_v_konfiguraci_hlasi_srozumitelnou_chybu()
    {
        ArrangeLogin("FN Motol", "Jiny");
        var api = CreateApi(accountName: null);

        var error = Assert.Throws<InvalidOperationException>(() => api.GetReaders());

        Assert.Contains("více účtů", error.Message);
        Assert.Contains("FN Motol", error.Message);
        Assert.Contains("Jiny", error.Message);
    }

    [Fact]
    public void Pristupove_urovne_bez_jednoznacneho_uctu_se_vrati_za_vsechny_ucty()
    {
        ArrangeLogin("FN Motol", "Jiny");
        App.OutValues["GetAllAccessLevels#0"] = Array.Empty<object>();

        CreateApi(accountName: null).GetAccessLevels();

        Assert.NotNull(_com.Call("GetAllAccessLevels"));
    }

    [Fact]
    public void Dohledany_ucet_se_pta_WinPaku_jen_jednou()
    {
        ArrangeLogin("FN Motol");
        var api = CreateApi(accountName: null);

        api.GetReaders();
        api.GetReaders();

        Assert.Single(_com.Calls.Where(c => c.Method == "GetAccounts"));
    }

    // ---------- Systémové údaje ----------

    [Fact]
    public void Systemove_udaje_vrati_co_jde_a_vypisou_odmitnuta_volani()
    {
        ArrangeLogin("FN Motol");
        App.OutValues["GetWPDSN#0"] = "WINPAK";
        App.OutValues["GetCurrentOperator#0"] = 5;
        App.OutValues["GetCurrentOperator#1"] = "acs-service";
        App.Throws["GetWPDBServerTZ"] = new COMException("Not implemented", unchecked((int)0x80004001));

        var info = CreateApi(accountName: null).GetSystemInfo();

        Assert.Equal("WINPAK", info.DataSourceName);
        Assert.Equal("acs-service", info.CurrentOperator?.Name);
        Assert.Null(info.ServerTimeZone);
        var problem = Assert.Single(info.Problems!);
        Assert.Contains("časová zóna serveru", problem);
        Assert.Contains("Not implemented", problem);
    }

    // ---------- Rozbalení chyb COM ----------
    // ComDispatch je označený jen pro Windows kvůli COM; nad běžným .NET objektem
    // funguje reflexe stejně a právě tak se dá chování InvokeMember ověřit i na Linuxu.
#pragma warning disable CA1416

    [Fact]
    public void Chyba_z_InvokeMember_se_rozbali_na_skutecnou_hlasku_s_HRESULT()
    {
        // Skutečný ComDispatch nad .NET objektem: InvokeMember zabalí výjimku cíle
        // do TargetInvocationException přesně jako u COM objektu.
        var dispatch = new ComDispatch(new Failing());

        var error = Assert.Throws<ComCallException>(() => dispatch.Invoke("Explode", []));

        Assert.Equal("Explode", error.Member);
        Assert.Contains("Explode", error.Message);
        Assert.Contains("Server odmítl", error.Message);
        Assert.Contains("HRESULT 0x80004005", error.Message);
        Assert.IsType<COMException>(error.InnerException);
        Assert.DoesNotContain("target of an invocation", error.Message);
    }

    [Fact]
    public void Vlastnost_ktera_selze_se_rozbali_stejne()
    {
        var dispatch = new ComDispatch(new Failing());

        var error = Assert.Throws<ComCallException>(() => dispatch.GetProperty("Broken"));

        Assert.Equal("Broken", error.Member);
    }

#pragma warning restore CA1416

    public sealed class Failing
    {
        public void Explode() => throw new COMException("Server odmítl požadavek", unchecked((int)0x80004005));

        public int Broken => throw new InvalidOperationException("vlastnost není k dispozici");
    }
}
