using System.Runtime.InteropServices;
using Acs.WinPakConnector.Providers.Com;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acs.WinPakConnector.Tests;

/// <summary>
/// Počet COM roundtripů — to je to, co dělá dotazování WIN-PAKu pomalé. Každé
/// volání přes pozdní vazbu a COM+ stojí řádově milisekundy až desítky, a při
/// 785 čtečkách nebo tisících držitelů rozhoduje, jestli se volá jednou, nebo
/// jednou za každý záznam.
/// </summary>
public sealed class ComCallCountTests
{
    private const string AppProgId = "NCIHelper.Application";
    private readonly FakeComFactory _com = new();

    private FakeComDispatch App => (FakeComDispatch)_com.Create(AppProgId);

    private WinPakDatabaseApi CreateApi() => new(_com, new WinPakComOptions
    {
        UserName = "svc", Password = "x", AccountName = "FN Motol", SubAccountName = "Default",
    });

    private void ArrangeLogin()
    {
        App.OutValues["Login#3"] = 42;
        App.OutValues["ConnectWPDatabase#3"] = 0;
    }

    private int Calls(string method) => _com.Calls.Count(c => c.Method == method);

    [Fact]
    public void Ctecky_se_nactou_dvema_volanimi_bez_ohledu_na_pocet()
    {
        ArrangeLogin();
        App.OutValues["GetReadersByAccountName#1"] = Enumerable.Range(1, 50)
            .Select(i => (object)_com.Record($"r{i}", ("HWDeviceID", (long)(1000 + i)), ("DeviceID", (long)(500 + i)), ("DeviceName", $"Čtečka {i}")))
            .ToArray();
        App.OutValues["GetADVDetailsByAccountName#1"] = Enumerable.Range(1, 50)
            .Select(i => (object)_com.Record($"d{i}", ("HWDeviceID", (long)(1000 + i)), ("DeviceID", (long)(500 + i)), ("DeviceName", $"Panel {i}")))
            .ToArray();

        var readers = CreateApi().GetReaders();

        Assert.Equal(50, readers.Count);
        Assert.Equal("Panel 7", readers[6].PanelName);
        Assert.Equal(1, Calls("GetReadersByAccountName"));
        Assert.Equal(1, Calls("GetADVDetailsByAccountName"));
        // Dřív: 50 × GetDevNameByDeviceID (a s opakováním po Type mismatch 100).
        Assert.Equal(0, Calls("GetDevNameByDeviceID"));
    }

    [Fact]
    public void Zarizeni_ktere_ve_vypisu_chybi_se_dohleda_jednotlive()
    {
        ArrangeLogin();
        App.OutValues["GetReadersByAccountName#1"] = new object[]
        {
            _com.Record("r1", ("HWDeviceID", 1001L), ("DeviceID", 501L), ("DeviceName", "A")),
        };
        App.OutValues["GetADVDetailsByAccountName#1"] = Array.Empty<object>();
        App.OutValues["GetDevNameByDeviceID#1"] = "Panel X";

        var reader = Assert.Single(CreateApi().GetReaders());

        Assert.Equal("Panel X", reader.PanelName);
        Assert.Equal(1, Calls("GetDevNameByDeviceID"));
    }

    [Fact]
    public void Drzitele_se_nactou_dvema_volanimi_karty_jednim_vypisem()
    {
        ArrangeLogin();
        App.OutValues["GetCardHoldersByAccountName#2"] = Enumerable.Range(1, 40)
            .Select(i => (object)_com.Record($"h{i}", ("CardHolderID", (long)i), ("FirstName", "Jan"), ("LastName", $"Novák {i}")))
            .ToArray();
        App.OutValues["GetCardsByAccountName#2"] = Enumerable.Range(1, 40)
            .Select(i => (object)_com.Record($"c{i}", ("CardNumber", $"10{i:000}"), ("CardID", (long)(100 + i)),
                ("CardHolderID", (long)i), ("CardStatus", 1), ("AccessLevels", new object[] { 1L })))
            .ToArray();

        var holders = CreateApi().GetCardHolders();

        Assert.Equal(40, holders.Count);
        Assert.Single(holders[4].Cards);
        Assert.Equal("10005", holders[4].Cards[0].CardNumber);
        Assert.Equal(1, Calls("GetCardHoldersByAccountName"));
        Assert.Equal(1, Calls("GetCardsByAccountName"));
        // Dřív: 40 × GetCardsByCHID.
        Assert.Equal(0, Calls("GetCardsByCHID"));
    }

    [Fact]
    public void Jeden_drzitel_si_karty_dal_bere_cilene()
    {
        ArrangeLogin();
        App.OutValues["GetCardHolderByCardHolderID#1"] = new object[]
        {
            _com.Record("h", ("CardHolderID", 7L), ("FirstName", "Jan"), ("LastName", "Novák")),
        };
        App.OutValues["GetCardsByCHID#1"] = Array.Empty<object>();

        CreateApi().GetCardHolder("7");

        Assert.Equal(1, Calls("GetCardsByCHID"));
        Assert.Equal(0, Calls("GetCardsByAccountName"));
    }

    // ---------- Naučený tvar argumentů ----------

    [Fact]
    public void Po_prvnim_Type_mismatch_se_dalsi_volani_metody_uz_neopakuje()
    {
        var target = new CountingStringOut();
        var dispatch = new ComDispatch(target);

        dispatch.Invoke("GetDevNameByDeviceIDTest", [1, null]);
        dispatch.Invoke("GetDevNameByDeviceIDTest", [2, null]);
        dispatch.Invoke("GetDevNameByDeviceIDTest", [3, null]);

        // První volání: odmítnuto + opakováno (2). Další dvě: rovnou správně (1 + 1).
        Assert.Equal(4, target.Calls);
    }

    public sealed class CountingStringOut
    {
        public int Calls;

        public void GetDevNameByDeviceIDTest(int id, ref string? name)
        {
            Calls++;
            if (name is null)
                throw new COMException("Type mismatch.", unchecked((int)0x80020005));
            name = $"Panel {id}";
        }
    }

    // ---------- Cache číselníků ----------

    [Fact]
    public async Task Opakovane_cteni_ctecek_v_provideru_jde_z_pameti()
    {
        ArrangeLogin();
        App.OutValues["GetReadersByAccountName#1"] = new object[]
        {
            _com.Record("r1", ("HWDeviceID", 1001L), ("DeviceID", 501L), ("DeviceName", "A")),
        };
        App.OutValues["GetADVDetailsByAccountName#1"] = Array.Empty<object>();
        App.OutValues["GetDevNameByDeviceID#1"] = "P";
        var provider = new ComWinPakProvider(
            Options.Create(new WinPakComOptions { UserName = "svc", Password = "x", AccountName = "FN Motol" }), _com);

        await provider.GetReadersAsync(CancellationToken.None);
        await provider.GetReadersAsync(CancellationToken.None);
        await provider.GetReadersAsync(CancellationToken.None);

        Assert.Equal(1, Calls("GetReadersByAccountName"));
    }

    [Fact]
    public async Task Zapis_do_ciselniku_cache_zahodi()
    {
        ArrangeLogin();
        App.OutValues["GetAccessLevelsByAccountName#2"] = new object[]
        {
            _com.Record("al", ("AccessLevelID", 1L), ("AccessLevelName", "Vše")),
        };
        var provider = new ComWinPakProvider(
            Options.Create(new WinPakComOptions { UserName = "svc", Password = "x", AccountName = "FN Motol" }), _com);

        await provider.GetAccessLevelsAsync(CancellationToken.None);
        await provider.DeleteAccessLevelAsync("Vše", CancellationToken.None);
        await provider.GetAccessLevelsAsync(CancellationToken.None);

        Assert.Equal(2, Calls("GetAccessLevelsByAccountName"));
    }
}
