using Acs.WinPakConnector.Models;
using Acs.WinPakConnector.Providers.Com;
using Xunit;

namespace Acs.WinPakConnector.Tests;

/// <summary>
/// Ověřuje, že obálka volá Database Server API přesně podle příručky WIN-PAK 4.9 —
/// názvy metod, pořadí parametrů a čtení <c>[out]</c> hodnot.
/// </summary>
public sealed class WinPakDatabaseApiTests
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

    private WinPakDatabaseApi CreateApi() => new(_com, _options);

    private FakeComDispatch App => (FakeComDispatch)_com.Create(AppProgId);

    /// <summary>Přihlášení vrací id operátora a připojení k DB hlásí úspěch.</summary>
    private void ArrangeSuccessfulLogin(int userId = 42)
    {
        App.OutValues["Login#3"] = userId;
        App.OutValues["ConnectWPDatabase#3"] = 0;
    }

    [Fact]
    public void Prihlaseni_vola_Login_a_ConnectWPDatabase_se_spravnymi_parametry()
    {
        ArrangeSuccessfulLogin(userId: 7);

        CreateApi().EnsureSession();

        var login = _com.Call("Login");
        // Čtvrtý parametr je [out] id operátora — na vstupu je nula, WIN-PAK ho vyplní.
        Assert.Equal([_options.UserName, _options.Password, _options.Domain, 0], login.Args);

        var connect = _com.Call("ConnectWPDatabase");
        Assert.Equal(_options.UserName, connect.Args[0]);
        Assert.Equal(_options.Domain, connect.Args[2]);
        // Poslední parametr je id operátora získané z Login.
        Assert.Equal(7, connect.Args[4]);
    }

    [Fact]
    public void Prihlaseni_probehne_jen_jednou()
    {
        ArrangeSuccessfulLogin();
        var api = CreateApi();

        api.EnsureSession();
        api.EnsureSession();

        Assert.Single(_com.AllCalls("Login"));
    }

    [Fact]
    public void Zaporne_id_operatora_znamena_neuspesne_prihlaseni()
    {
        App.OutValues["Login#3"] = -1;

        var error = Assert.Throws<InvalidOperationException>(() => CreateApi().EnsureSession());
        Assert.Contains("Přihlášení", error.Message);
        Assert.Empty(_com.AllCalls("ConnectWPDatabase"));
    }

    [Fact]
    public void Status_minus_dva_z_ConnectWPDatabase_znamena_nedostupnou_databazi()
    {
        App.OutValues["Login#3"] = 5;
        App.OutValues["ConnectWPDatabase#3"] = -2;

        var error = Assert.Throws<InvalidOperationException>(() => CreateApi().EnsureSession());
        Assert.Contains("databázi", error.Message);
    }

    [Fact]
    public void Ctecky_se_ctou_pres_GetReadersByAccountName_a_id_je_HWDeviceID()
    {
        ArrangeSuccessfulLogin();
        App.OutValues["GetReadersByAccountName#1"] = new object[]
        {
            _com.Record("reader",
                ("HWDeviceID", 23L), ("DeviceName", "Hlavní vchod"),
                ("DeviceDesc", "Vstup A"), ("DeviceID", 4L)),
        };
        App.OutValues["GetDevNameByDeviceID#1"] = "PRO4200-A1";

        var readers = CreateApi().GetReaders();

        Assert.Equal(_options.AccountName, _com.Call("GetReadersByAccountName").Args[0]);
        var reader = Assert.Single(readers);
        // Comm API adresuje dveře přes HWDeviceID, proto je to i id čtečky.
        Assert.Equal("23", reader.Id);
        Assert.Equal("Hlavní vchod", reader.Name);
        Assert.Equal("PRO4200-A1", reader.PanelName);
    }

    [Fact]
    public void Nazev_panelu_se_pro_stejne_DeviceID_dotahuje_jen_jednou()
    {
        ArrangeSuccessfulLogin();
        App.OutValues["GetReadersByAccountName#1"] = new object[]
        {
            _com.Record("r1", ("HWDeviceID", 1L), ("DeviceName", "A"), ("DeviceID", 4L)),
            _com.Record("r2", ("HWDeviceID", 2L), ("DeviceName", "B"), ("DeviceID", 4L)),
        };
        App.OutValues["GetDevNameByDeviceID#1"] = "PRO4200-A1";

        CreateApi().GetReaders();

        Assert.Single(_com.AllCalls("GetDevNameByDeviceID"));
    }

    [Fact]
    public void Pristupove_urovne_se_pro_ucet_ctou_pres_GetAccessLevelsByAccountName()
    {
        ArrangeSuccessfulLogin();
        App.OutValues["GetAccessLevelsByAccountName#2"] = new object[]
        {
            _com.Record("al", ("AccessLevelID", 3L), ("AccessLevelName", "Serverovna"), ("AccessLevelDesc", "A-105")),
        };

        var levels = CreateApi().GetAccessLevels();

        var call = _com.Call("GetAccessLevelsByAccountName");
        Assert.Equal([_options.AccountName, _options.SubAccountName, null], call.Args);
        Assert.Equal("3", Assert.Single(levels).Id);
    }

    [Fact]
    public void Bez_uctu_se_pouzije_GetAllAccessLevels()
    {
        _options.AccountName = "";
        ArrangeSuccessfulLogin();
        App.OutValues["GetAllAccessLevels#0"] = new object[]
        {
            _com.Record("al", ("AccessLevelID", 1L), ("AccessLevelName", "Vše")),
        };

        var levels = CreateApi().GetAccessLevels();

        Assert.Single(levels);
        Assert.Empty(_com.AllCalls("GetAccessLevelsByAccountName"));
    }

    [Fact]
    public void Drzitel_ma_sjednoceni_urovni_svych_karet()
    {
        ArrangeSuccessfulLogin();
        App.OutValues["GetCardHoldersByAccountName#2"] = new object[]
        {
            _com.Record("holder",
                ("CardHolderID", 1001L), ("FirstName", "Jan"), ("LastName", "Novák"), ("NoteField", "IT")),
        };
        App.OutValues["GetCardsByCHID#1"] = new object[]
        {
            _com.Record("card1",
                ("CardNumber", "100234"), ("CardID", 5L), ("CardHolderID", 1001L),
                ("CardStatus", 1), ("AccessLevels", new object[] { 1L, 2L })),
            _com.Record("card2",
                ("CardNumber", "100235"), ("CardID", 6L), ("CardHolderID", 1001L),
                ("CardStatus", 1), ("AccessLevels", new object[] { 2L, 3L })),
        };

        var holder = Assert.Single(CreateApi().GetCardHolders());

        Assert.Equal("1001", holder.Id);
        Assert.Equal(2, holder.Cards.Count);
        Assert.Equal(["1", "2", "3"], holder.AccessLevelIds.Order());
    }

    [Fact]
    public void Karta_se_cte_pres_GetCardbyCardNumber_vcetne_uctu()
    {
        ArrangeSuccessfulLogin();
        App.OutValues["GetCardbyCardNumber#3"] = new object[]
        {
            _com.Record("card",
                ("CardNumber", "100234"), ("CardID", 9L), ("CardStatus", 2), ("Issue", 3),
                ("ActivationDate", new DateTime(2024, 1, 1)), ("AccessLevels", new object[] { 7L })),
        };

        var card = CreateApi().GetCard("100234");

        Assert.Equal(["100234", _options.AccountName, _options.SubAccountName, null],
            _com.Call("GetCardbyCardNumber").Args);
        Assert.NotNull(card);
        Assert.Equal(CardStatus.Inactive, card.Status);
        Assert.Equal(3, card.Issue);
        Assert.Equal(new DateTime(2024, 1, 1), card.ActivationDate);
        Assert.Equal(["7"], card.AccessLevelIds);
    }

    [Fact]
    public void Neexistujici_karta_vrati_null()
    {
        ArrangeSuccessfulLogin();

        Assert.Null(CreateApi().GetCard("999"));
    }

    [Fact]
    public void AddUpdateCard_ma_14_parametru_v_poradi_podle_prirucky()
    {
        ArrangeSuccessfulLogin();
        App.OutValues["GetCardbyCardNumber#3"] = new object[]
        {
            _com.Record("card", ("CardNumber", "100234"), ("CardID", 9L), ("CardHolderID", 1001L)),
        };

        CreateApi().UpsertCard("100234", new UpsertCardRequest(
            CardHolderId: "1001",
            Status: CardStatus.Active,
            Issue: 2,
            ActivationDate: new DateTime(2026, 1, 1),
            ExpirationDate: new DateTime(2027, 1, 1),
            Pin: "1234",
            AccessLevelIds: ["4", "5"]), accountId: 1, subAccountId: 2);

        var args = _com.Call("AddUpdateCard").Args;
        Assert.Equal(14, args.Length);
        Assert.Equal(9L, args[0]);                          // dwRecordID z existující karty
        Assert.Equal("100234", args[1]);                    // sCardNo
        Assert.Equal(1L, args[2]);                          // lAccountID
        Assert.Equal(2L, args[3]);                          // lSubAccountID
        Assert.Equal(1, args[4]);                           // lCardStatus (Active)
        Assert.Equal(2, args[5]);                           // lissue
        Assert.Equal(1001L, args[6]);                       // lCardHolderID
        Assert.Equal("1234", args[7]);                      // Pin1
        Assert.Equal(new DateTime(2026, 1, 1), args[8]);    // dtActivationDate
        Assert.Equal(new DateTime(2027, 1, 1), args[9]);    // dtExpirationDate
        Assert.Equal(0, args[10]);                          // Backdrop1ID
        Assert.Equal(0, args[11]);                          // Backdrop2ID
        Assert.Equal(true, args[12]);                       // bMultiple (dvě úrovně)
        Assert.Equal(new long[] { 4, 5 }, args[13]);        // alAccessLevelIDs
    }

    [Fact]
    public void Nova_karta_ma_recordId_nula()
    {
        ArrangeSuccessfulLogin();

        CreateApi().UpsertCard("777", new UpsertCardRequest(CardHolderId: "1001"), accountId: 1, subAccountId: 0);

        var args = _com.Call("AddUpdateCard").Args;
        Assert.Equal(0L, args[0]);
        Assert.Equal(false, args[12]);   // jedna nebo žádná úroveň → bMultiple = false
    }

    [Fact]
    public void Zalozeni_drzitele_vraci_id_prirazene_WinPakem()
    {
        ArrangeSuccessfulLogin();
        var holderObject = (FakeComDispatch)_com.Create(_options.CardHolderProgId);
        holderObject.Properties["CardHolderID"] = 2001L;
        App.OutValues["AddCardHolder#1"] = 0;

        var id = CreateApi().AddCardHolder(new UpsertCardHolderRequest("Jan", "Novák", "IT"));

        Assert.Equal("2001", id);
        Assert.Equal("Jan", holderObject.Properties["FirstName"]);
        Assert.Equal("Novák", holderObject.Properties["LastName"]);
        Assert.Equal(_options.AccountName, holderObject.Properties["AccountName"]);
    }

    [Fact]
    public void Chybovy_status_zalozeni_drzitele_se_prelozi_do_hlasky()
    {
        ArrangeSuccessfulLogin();
        _com.Create(_options.CardHolderProgId);
        App.OutValues["AddCardHolder#1"] = 301;

        var error = Assert.Throws<WinPakOperationException>(
            () => CreateApi().AddCardHolder(new UpsertCardHolderRequest("", "", null)));

        Assert.Equal(301, error.Status);
        Assert.Contains("jméno nebo příjmení", error.Message);
    }

    [Fact]
    public void Chybovy_status_zruseni_karty_se_prelozi_do_hlasky()
    {
        ArrangeSuccessfulLogin();
        App.OutValues["DeleteCard#3"] = 102;

        var error = Assert.Throws<WinPakOperationException>(() => CreateApi().DeleteCard("abc"));

        Assert.Equal(102, error.Status);
        Assert.Contains("neplatné číslo karty", error.Message);
    }

    [Fact]
    public void Id_uctu_se_dohleda_podle_nazvu_z_konfigurace()
    {
        ArrangeSuccessfulLogin();
        App.OutValues["GetAccounts#0"] = new object[]
        {
            _com.Record("acc1", ("AccountID", 1L), ("AccountName", "Jiny")),
            _com.Record("acc2", ("AccountID", 8L), ("AccountName", "FNMH")),
        };
        App.OutValues["GetSubAccountsByAccountID#1"] = new object[]
        {
            _com.Record("sub", ("AccountID", 3L), ("AccountName", "Default")),
        };

        var (accountId, subAccountId) = CreateApi().ResolveAccountIds();

        Assert.Equal(8, accountId);
        Assert.Equal(3, subAccountId);
    }
}
