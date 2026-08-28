using Acs.WinPakConnector.Models;
using Acs.WinPakConnector.Providers.Com;
using Xunit;

namespace Acs.WinPakConnector.Tests;

/// <summary>
/// Mapování rozšířené části Database Server API — časové zóny, svátky, hardware,
/// přístupové úrovně, hromadné operace s kartami a systémové údaje.
/// Kontroluje se název volání i pořadí parametrů podle příručky WIN-PAK 4.9.
/// </summary>
public sealed class WinPakCatalogApiTests
{
    private const string AppProgId = "NCIHelper.Application";

    private readonly FakeComFactory _com = new();
    private readonly WinPakComOptions _options = new()
    {
        UserName = "acs-service",
        Password = "tajne",
        Domain = "FNMH",
        AccountName = "FNMH",
        SubAccountName = "Default",
    };

    private FakeComDispatch App => (FakeComDispatch)_com.Create(AppProgId);

    private WinPakDatabaseApi CreateApi()
    {
        App.OutValues["Login#3"] = 42;
        App.OutValues["ConnectWPDatabase#3"] = 0;
        return new WinPakDatabaseApi(_com, _options);
    }

    /// <summary>Většina zápisových volání potřebuje číselné id účtu — připravíme je.</summary>
    private void ArrangeAccounts(long accountId = 8, long subAccountId = 3)
    {
        App.OutValues["GetAccounts#0"] = new object[]
        {
            _com.Record("acc", ("AccountID", accountId), ("AccountName", _options.AccountName)),
        };
        App.OutValues["GetSubAccountsByAccountID#1"] = new object[]
        {
            _com.Record("sub", ("AccountID", subAccountId), ("AccountName", _options.SubAccountName)),
        };
    }

    // ---------- Časové zóny ----------

    [Fact]
    public void Casove_zony_uctu_se_ctou_pres_GetTimeZonesByAccountName()
    {
        App.OutValues["GetTimeZonesByAccountName#1"] = new object[]
        {
            _com.Record("tz", ("TimeZoneID", 4L), ("TimeZoneName", "Pracovní doba"),
                ("TimeZoneDesc", "Po–Pá"), ("AccountName", "FNMH")),
        };

        var zones = CreateApi().GetTimeZones();

        Assert.Equal(_options.AccountName, _com.Call("GetTimeZonesByAccountName").Args[0]);
        var zone = Assert.Single(zones);
        Assert.Equal("4", zone.Id);
        Assert.Equal("Pracovní doba", zone.Name);
    }

    [Fact]
    public void Bez_uctu_se_pouzije_GetAllTimezones()
    {
        _options.AccountName = "";
        App.OutValues["GetAllTimezones#0"] = new object[]
        {
            _com.Record("tz", ("TimeZoneID", 1L), ("TimeZoneName", "Nepřetržitě")),
        };

        Assert.Single(CreateApi().GetTimeZones());
        Assert.Empty(_com.AllCalls("GetTimeZonesByAccountName"));
    }

    [Fact]
    public void Zalozeni_casove_zony_vraci_id_z_out_parametru()
    {
        App.OutValues["AddTimezone#1"] = 17;
        App.OutValues["AddTimezone#2"] = 0;

        var id = CreateApi().AddTimeZone(new UpsertTimeZoneRequest("Noční", "22:00–06:00"));

        Assert.Equal("17", id);
        var zone = (FakeComDispatch)_com.Create(_options.TimeZoneProgId);
        Assert.Equal("Noční", zone.Properties["TimeZoneName"]);
        Assert.Equal("22:00–06:00", zone.Properties["TimeZoneDesc"]);
    }

    [Fact]
    public void Intervaly_casove_zony_se_posilaji_jako_trojice_den_od_do()
    {
        ArrangeAccounts();
        App.OutValues["ConfigureTimeZoneRange#3"] = 0;

        CreateApi().ConfigureTimeZoneRanges("4",
        [
            new TimeZoneRangeRequest(1, "06:00", "18:00"),
            new TimeZoneRangeRequest(2, "08:00", "12:00"),
        ]);

        var args = _com.Call("ConfigureTimeZoneRange").Args;
        Assert.Equal(8L, args[0]);   // lAcctID
        Assert.Equal(4L, args[1]);   // lTimeZoneID
        Assert.Equal(new object?[] { 1, "06:00", "18:00", 2, "08:00", "12:00" }, args[2]);
    }

    [Fact]
    public void Chybovy_status_pri_ruseni_casove_zony_skonci_vyjimkou()
    {
        App.OutValues["DeleteTimeZone#1"] = 1;

        var error = Assert.Throws<WinPakOperationException>(() => CreateApi().DeleteTimeZone("4"));
        Assert.Equal(1, error.Status);
    }

    // ---------- Svátky ----------

    [Fact]
    public void Zalozeni_svatku_naplni_objekt_a_vrati_id()
    {
        App.OutValues["AddHoliday#1"] = 55;
        App.OutValues["AddHoliday#2"] = 0;

        var id = CreateApi().AddHoliday(new UpsertHolidayRequest("Nový rok", 2026, 1, 1, Type: 0, AppliesToAllYears: true));

        Assert.Equal("55", id);
        var holiday = (FakeComDispatch)_com.Create(_options.HolidayProgId);
        Assert.Equal("Nový rok", holiday.Properties["Name"]);
        Assert.Equal(2026, holiday.Properties["Year"]);
        Assert.Equal(1, holiday.Properties["Month"]);
        Assert.Equal(true, holiday.Properties["ApplyAllYears"]);
    }

    [Fact]
    public void Skupina_svatku_se_zaklada_se_seznamem_svatku_i_master_svatku()
    {
        ArrangeAccounts();
        App.OutValues["AddHolidayGroup#3"] = 0;

        CreateApi().AddHolidayGroup(new UpsertHolidayGroupRequest("Státní svátky", ["1", "2"], ["9"]));

        var args = _com.Call("AddHolidayGroup").Args;
        Assert.Equal(new long[] { 1, 2 }, args[1]);
        Assert.Equal(new long[] { 9 }, args[2]);
    }

    [Fact]
    public void Skupiny_svatku_uctu_se_ctou_podle_id_uctu()
    {
        ArrangeAccounts();
        App.OutValues["GetHolidayGroupsByAcctID#1"] = new object[]
        {
            _com.Record("grp", ("HolGrpID", 3L), ("HolGrpName", "Státní svátky"), ("AccountId", 8L)),
        };

        var groups = CreateApi().GetHolidayGroups();

        Assert.Equal(8L, _com.Call("GetHolidayGroupsByAcctID").Args[0]);
        Assert.Equal("Státní svátky", Assert.Single(groups).Name);
    }

    // ---------- Přístupové úrovně ----------

    [Fact]
    public void Zalozeni_pristupove_urovne_posila_ucty_i_nazev_uctu()
    {
        ArrangeAccounts();
        App.OutValues["CreateAccessLevel#4"] = 0;

        CreateApi().CreateAccessLevel(new CreateAccessLevelRequest("Serverovna", "A-105"));

        var args = _com.Call("CreateAccessLevel").Args;
        Assert.Equal("Serverovna", args[0]);
        Assert.Equal("A-105", args[1]);
        Assert.Equal(new long[] { 8 }, args[2]);
        Assert.Equal(_options.AccountName, args[3]);
    }

    [Fact]
    public void AddUpdateAL_ma_osm_parametru_v_poradi_podle_prirucky()
    {
        ArrangeAccounts();

        CreateApi().UpsertAccessLevel("5", new UpsertAccessLevelRequest(
            "Serverovna", "A-105", SubAccountIds: ["3"], ReaderIds: ["11", "12"],
            ReaderTimeZoneIds: ["1", "1"], ReaderGroupIds: ["7"]));

        var args = _com.Call("AddUpdateAL").Args;
        Assert.Equal(8, args.Length);
        Assert.Equal(5L, args[0]);
        Assert.Equal("Serverovna", args[1]);
        Assert.Equal("A-105", args[2]);
        Assert.Equal(8L, args[3]);
        Assert.Equal(new long[] { 3 }, args[4]);
        Assert.Equal(new long[] { 11, 12 }, args[5]);
        Assert.Equal(new long[] { 1 }, args[6]);   // duplicity se zahazují
        Assert.Equal(new long[] { 7 }, args[7]);
    }

    [Fact]
    public void Konfigurace_urovne_posila_ctecky_jako_pole_nazvu()
    {
        App.OutValues["ConfigureAccessLevel#4"] = 0;

        CreateApi().ConfigureAccessLevel("Serverovna",
            new ConfigureAccessLevelRequest(["Chodba A-101", "Serverovna A-105"], "Nepřetržitě"));

        var args = _com.Call("ConfigureAccessLevel").Args;
        Assert.Equal("Serverovna", args[0]);
        Assert.Equal(_options.AccountName, args[1]);
        Assert.Equal(new object?[] { "Chodba A-101", "Serverovna A-105" }, args[2]);
        Assert.Equal("Nepřetržitě", args[3]);
    }

    [Fact]
    public void Izolace_urovne_vraci_karty_a_kontroluje_status()
    {
        App.OutValues["IsolateAccessLevel#2"] = new object[]
        {
            _com.Record("card", ("CardNumber", "100234"), ("CardID", 9L)),
        };
        App.OutValues["IsolateAccessLevel#3"] = 0;

        var cards = CreateApi().IsolateAccessLevel("Serverovna");

        Assert.Equal("100234", Assert.Single(cards).CardNumber);
    }

    [Fact]
    public void Preřazeni_urovne_posle_karty_ziskane_izolaci()
    {
        App.OutValues["IsolateAccessLevel#2"] = new object[]
        {
            _com.Record("card", ("CardNumber", "100234"), ("CardID", 9L)),
        };
        App.OutValues["IsolateAccessLevel#3"] = 0;
        App.OutValues["ReassignAccessLevel#4"] = 0;

        CreateApi().ReassignAccessLevel("Serverovna", new ReassignAccessLevelRequest("Vstup A"));

        var args = _com.Call("ReassignAccessLevel").Args;
        Assert.Equal(_options.AccountName, args[0]);
        Assert.Equal("Serverovna", args[1]);
        Assert.Equal("Vstup A", args[2]);
        Assert.Equal(new object?[] { 9L }, args[3]);
    }

    // ---------- Karty hromadně ----------

    [Fact]
    public void BulkAddCards_ma_11_parametru_vcetne_operatora()
    {
        ArrangeAccounts();
        App.OutValues["GetCurrentOperator#0"] = 4;
        App.OutValues["GetCurrentOperator#1"] = "acs-service";

        CreateApi().BulkAddCards(new BulkAddCardsRequest("1000", "1010",
            CardStatus.Active, new DateTime(2026, 1, 1), new DateTime(2027, 1, 1), ["4", "5"]));

        var args = _com.Call("BulkAddCards").Args;
        Assert.Equal(11, args.Length);
        Assert.Equal("1000", args[0]);
        Assert.Equal("1010", args[1]);
        Assert.Equal(8L, args[2]);
        Assert.Equal(3L, args[3]);
        Assert.Equal(1, args[4]);
        Assert.Equal(new DateTime(2026, 1, 1), args[5]);
        Assert.Equal(new DateTime(2027, 1, 1), args[6]);
        Assert.Equal(4, args[7]);                       // lOperID
        Assert.Equal("acs-service", args[8]);           // sOpName
        Assert.Equal(true, args[9]);                    // bMultiple
        Assert.Equal(new long[] { 4, 5 }, args[10]);
    }

    [Fact]
    public void BulkDeleteCards_posila_rozsah_ucet_a_operatora()
    {
        ArrangeAccounts();
        App.OutValues["GetCurrentOperator#0"] = 4;
        App.OutValues["GetCurrentOperator#1"] = "acs-service";

        CreateApi().BulkDeleteCards(new BulkDeleteCardsRequest("1000", "1010"));

        Assert.Equal(["1000", "1010", 8L, 3L, 4, "acs-service"], _com.Call("BulkDeleteCards").Args);
    }

    // ---------- Držitelé ----------

    [Fact]
    public void Smazani_drzitele_posila_priznaky_karet_a_obrazku()
    {
        App.OutValues["DeleteCardHolder#3"] = 0;

        CreateApi().DeleteCardHolder("1001", new DeleteCardHolderOptions(DeleteCards: true, DeleteImages: false));

        Assert.Equal([1001L, 1, 0, 0], _com.Call("DeleteCardHolder").Args);
    }

    [Fact]
    public void Vyhledavani_drzitelu_posila_pole_poli_hodnot_a_porovnani()
    {
        App.OutValues["GetCardHoldersOnSearch#5"] = Array.Empty<object>();

        CreateApi().SearchCardHolders(new CardHolderSearchRequest(
        [
            new CardHolderSearchCriterion("LastName", "Novák", 1),
            new CardHolderSearchCriterion("FirstName", "Jan", 0),
        ]));

        var args = _com.Call("GetCardHoldersOnSearch").Args;
        Assert.Equal(new object?[] { "LastName", "FirstName" }, args[2]);
        Assert.Equal(new object?[] { "Novák", "Jan" }, args[3]);
        Assert.Equal(new object?[] { 1, 0 }, args[4]);
    }

    [Fact]
    public void Fotka_se_cte_i_s_velikosti_a_vraci_se_v_base64()
    {
        App.OutValues["GetPhotoSize#2"] = 3;
        App.OutValues["GetPhoto#2"] = new byte[] { 1, 2, 3 };

        var photo = CreateApi().GetPhoto("1001", 0);

        Assert.Equal(3, photo.Size);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), photo.ContentBase64);
        Assert.Equal([1001L, 0, null], _com.Call("GetPhoto").Args);
    }

    [Fact]
    public void Import_podpisu_posila_dekodovane_byty()
    {
        CreateApi().ImportSignature("1001", 1, Convert.ToBase64String([9, 8]));

        var args = _com.Call("ImportSig").Args;
        Assert.Equal(1001L, args[0]);
        Assert.Equal(1, args[1]);
        Assert.Equal(new byte[] { 9, 8 }, args[2]);
    }

    // ---------- Hardware ----------

    [Fact]
    public void Panely_se_ctou_podle_id_uctu()
    {
        ArrangeAccounts();
        App.OutValues["GetPanelsByAcctID#1"] = new object[]
        {
            _com.Record("panel", ("DeviceID", 4L), ("DeviceName", "PRO4200-A1"), ("DeviceDesc", "Budova A")),
        };

        var panels = CreateApi().GetPanels();

        Assert.Equal(8L, _com.Call("GetPanelsByAcctID").Args[0]);
        Assert.Equal("PRO4200-A1", Assert.Single(panels).Name);
    }

    [Fact]
    public void Casova_zona_vystupu_se_nastavuje_variantou_bez_zamykani()
    {
        ArrangeAccounts();
        App.OutValues["ConfigureOutputTimezone#4"] = 0;

        CreateApi().ConfigureOutputTimeZone(panelId: 4, outputId: 11, timeZoneId: "2");

        Assert.Equal([8L, 4L, 11L, 2L, 0], _com.Call("ConfigureOutputTimezone").Args);
    }

    [Fact]
    public void Rozsirena_varianta_prida_priznak_zamknout_odemknout()
    {
        ArrangeAccounts();
        App.OutValues["ConfigureOutputTimezoneEx#5"] = 0;

        CreateApi().ConfigureOutputTimeZoneEx(panelId: 4, outputId: 11, lockUnlock: 1, timeZoneId: "2");

        Assert.Equal([8L, 4L, 11L, 1, 2L, 0], _com.Call("ConfigureOutputTimezoneEx").Args);
    }

    [Fact]
    public void Ctecky_v_pristupove_oblasti_se_ctou_podle_nazvu_vetve()
    {
        App.OutValues["GetReadersInAccessAreaBranch#2"] = new object[]
        {
            _com.Record("reader", ("HWDeviceID", 23L), ("DeviceName", "Hlavní vchod")),
        };

        var readers = CreateApi().GetReadersInAccessAreaBranch("Budova A");

        Assert.Equal([_options.AccountName, "Budova A", null], _com.Call("GetReadersInAccessAreaBranch").Args);
        Assert.Equal("23", Assert.Single(readers).Id);
    }

    // ---------- Systém ----------

    [Fact]
    public void Systemove_udaje_se_skladaji_z_vice_volani()
    {
        App.OutValues["GetWPDSN#0"] = "WINPAK";
        App.OutValues["GetWPDBServerTZ#0"] = "Central Europe Standard Time";
        App.OutValues["GetWPDBServerTZ#1"] = 1;
        App.OutValues["GetWPDBServerTZoffset#0"] = 60;
        App.OutValues["GetMaxCardNumberLength#0"] = 10;
        App.OutValues["GetCardNumeric#0"] = true;
        App.OutValues["GetAccessLevelType#0"] = 2;
        App.OutValues["GetCurrentOperator#0"] = 4;
        App.OutValues["GetCurrentOperator#1"] = "acs-service";
        App.OutValues["GetConfiguredWPDomains#0"] = new object[] { "FNMH", "TEST" };

        var info = CreateApi().GetSystemInfo();

        Assert.Equal("WINPAK", info.DataSourceName);
        Assert.Equal("Central Europe Standard Time", info.ServerTimeZone);
        Assert.True(info.DaylightSavingEnabled);
        Assert.Equal(60, info.ServerTimeZoneOffsetMinutes);
        Assert.Equal(10, info.MaxCardNumberLength);
        Assert.True(info.CardNumbersAreNumeric);
        Assert.Equal(2, info.AccessLevelType);
        Assert.Equal("acs-service", info.CurrentOperator?.Name);
        Assert.Equal(["FNMH", "TEST"], info.Domains);
    }

    [Fact]
    public void Odznak_kombinuje_data_a_rozmery()
    {
        App.OutValues["GetBadgeData#1"] = "<badge/>";
        App.OutValues["GetBadgeDimension#1"] = 54;
        App.OutValues["GetBadgeDimension#2"] = 86;

        var badge = CreateApi().GetBadge("7");

        Assert.Equal("<badge/>", badge.Data);
        Assert.Equal(54, badge.Height);
        Assert.Equal(86, badge.Width);
    }
}
