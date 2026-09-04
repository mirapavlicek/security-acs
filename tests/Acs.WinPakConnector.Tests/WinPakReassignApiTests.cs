using Acs.WinPakConnector.Models;
using Acs.WinPakConnector.Providers.Com;
using Xunit;

namespace Acs.WinPakConnector.Tests;

/// <summary>
/// Přeřazení časových zón a objektové varianty zápisu, které příručka uvádí
/// vedle novějších „AddUpdate“ volání.
/// </summary>
public sealed class WinPakReassignApiTests
{
    private const string AppProgId = "NCIHelper.Application";

    private readonly FakeComFactory _com = new();
    private readonly WinPakComOptions _options = new()
    {
        UserName = "acs-service",
        Password = "tajne",
        AccountName = "FNMH",
        SubAccountName = "Default",
    };

    private FakeComDispatch App => (FakeComDispatch)_com.Create(AppProgId);

    private WinPakDatabaseApi CreateApi()
    {
        App.OutValues["Login#3"] = 42;
        App.OutValues["ConnectWPDatabase#3"] = 0;
        App.OutValues["GetAccounts#0"] = new object[]
        {
            _com.Record("acc", ("AccountID", 8L), ("AccountName", "FNMH")),
        };
        App.OutValues["GetSubAccountsByAccountID#1"] = new object[]
        {
            _com.Record("sub", ("AccountID", 3L), ("AccountName", "Default")),
        };
        return new WinPakDatabaseApi(_com, _options);
    }

    [Fact]
    public void Prehled_pouziti_casove_zony_projde_vsechny_isolate_metody()
    {
        foreach (var method in new[]
                 {
                     "IsolateOperatorsForTZReassign", "IsolatePanelsForTZDelete",
                     "IsolateAccessLevelsForTZReassign", "IsolateActionGroupsForTZReassign",
                     "IsolateCardsForTZReassign", "IsolateADVsForTZReassign",
                 })
        {
            // Operátorská varianta má o parametr méně (bez účtu).
            var isOperators = method == "IsolateOperatorsForTZReassign";
            App.OutValues[$"{method}#{(isOperators ? 1 : 2)}"] = new object[]
            {
                _com.Record("item", ("TimeZoneID", 4L)),
            };
            App.OutValues[$"{method}#{(isOperators ? 2 : 3)}"] = 0;
        }

        var usage = CreateApi().GetTimeZoneUsage("4");

        Assert.Equal(["4"], usage.Operators);
        Assert.Equal(["4"], usage.Panels);
        Assert.Equal(["4"], usage.Cards);
        // Operátorská varianta se ptá jen na id zóny, ostatní i na účet.
        Assert.Equal(4L, _com.Call("IsolateOperatorsForTZReassign").Args[0]);
        Assert.Equal(8L, _com.Call("IsolateCardsForTZReassign").Args[0]);
        Assert.Equal(4L, _com.Call("IsolateCardsForTZReassign").Args[1]);
    }

    [Fact]
    public void Kandidati_na_prerazeni_maji_pro_operatory_vlastni_volani()
    {
        App.OutValues["GetTZsForOperatorReassign#1"] = new object[]
        {
            _com.Record("tz", ("TimeZoneID", 5L), ("TimeZoneName", "Nepřetržitě")),
        };
        App.OutValues["GetTZsForReassign#2"] = new object[]
        {
            _com.Record("tz", ("TimeZoneID", 6L), ("TimeZoneName", "Pracovní doba")),
        };
        var api = CreateApi();

        Assert.Equal("5", Assert.Single(api.GetTimeZonesForReassign("4", forOperators: true)).Id);
        Assert.Equal("6", Assert.Single(api.GetTimeZonesForReassign("4", forOperators: false)).Id);
        Assert.Equal(8L, _com.Call("GetTZsForReassign").Args[0]);
    }

    [Fact]
    public void Prerazeni_posila_kazdou_skupinu_vlastnim_volanim()
    {
        foreach (var method in new[] { "ReassignAccessLevelTZ", "ReassignActionGroupTZ", "ReassignCardTZ", "ReassignADVTZ" })
            App.OutValues[$"{method}#4"] = 0;
        App.OutValues["ReassignOperatorTZ#2"] = 0;

        CreateApi().ReassignTimeZone(new ReassignTimeZoneRequest(
            CurrentTimeZoneId: "4",
            NewTimeZoneId: "5",
            OperatorIds: ["1"],
            AccessLevelIds: ["2"],
            CardIds: ["3"]));

        // Operátoři: jen nová zóna a seznam.
        Assert.Equal([5L, new int[] { 1 }, 0], _com.Call("ReassignOperatorTZ").Args);
        // Ostatní: účet, stará zóna, nová zóna, seznam, status.
        Assert.Equal([8L, 4L, 5L, new int[] { 2 }, 0], _com.Call("ReassignAccessLevelTZ").Args);
        Assert.Equal([8L, 4L, 5L, new int[] { 3 }, 0], _com.Call("ReassignCardTZ").Args);
        // Skupiny bez id se nevolají vůbec.
        Assert.Empty(_com.AllCalls("ReassignActionGroupTZ"));
    }

    [Fact]
    public void Odebrani_zony_z_panelu_posila_seznam_panelu()
    {
        App.OutValues["DeletePanelTZ#3"] = 0;

        CreateApi().DeletePanelTimeZone("4", ["11", "12"]);

        Assert.Equal([8L, 4L, new int[] { 11, 12 }, 0], _com.Call("DeletePanelTZ").Args);
    }

    [Fact]
    public void Objektovy_zapis_karty_naplni_vlastnosti_a_zkontroluje_status()
    {
        App.OutValues["AddCard#1"] = 0;

        CreateApi().AddCard("100234", new UpsertCardRequest("1001", CardStatus.Active, 2,
            new DateTime(2026, 1, 1), null, "1234", ["4"]));

        var card = (FakeComDispatch)_com.Create(_options.CardProgId);
        Assert.Equal("100234", card.Properties["CardNumber"]);
        Assert.Equal(1001L, card.Properties["CardHolderID"]);
        Assert.Equal("1234", card.Properties["PIN1"]);
        Assert.Equal(new int[] { 4 }, card.Properties["AccessLevels"]);
    }

    [Fact]
    public void Rozsireny_zapis_karty_prida_NetAXS_parametry_na_konec()
    {
        CreateApi().UpsertCardEx("100234",
            new UpsertCardRequest("1001"),
            new NetAxsCardOptions(TemporaryCard: true, CardType: 2, UsageLimit: 5, LimitedCard: true, Trigger: 7),
            accountId: 8, subAccountId: 3);

        var args = _com.Call("AddUpdateCardEx").Args;
        Assert.Equal(19, args.Length);
        Assert.Equal(true, args[14]);        // bTempCard
        Assert.Equal((short)2, args[15]);    // iNXCardType
        Assert.Equal((short)5, args[16]);    // nUsageLimits
        Assert.Equal(true, args[17]);        // bLimitedCard
        Assert.Equal(7L, args[18]);          // lTrigger
    }

    [Fact]
    public void Objektovy_zapis_pristupove_urovne_naplni_nazev_i_ucet()
    {
        App.OutValues["AddAccessLevel#1"] = 0;

        CreateApi().AddAccessLevel(new CreateAccessLevelRequest("Serverovna", "A-105"));

        var level = (FakeComDispatch)_com.Create(_options.AccessLevelProgId);
        Assert.Equal("Serverovna", level.Properties["AccessLevelName"]);
        Assert.Equal("FNMH", level.Properties["AccountName"]);
    }

    [Fact]
    public void Ucet_lze_dohledat_podle_id_i_nazvu()
    {
        App.OutValues["GetAccountByAcctID#1"] = new object[]
        {
            _com.Record("acc", ("AccountID", 8L), ("AccountName", "FNMH")),
        };
        App.OutValues["GetAccountNameByAcctID#1"] = "FNMH";
        App.OutValues["GetSubAccountNameBySubAcctID#1"] = "Default";
        var api = CreateApi();

        Assert.Equal("FNMH", api.GetAccount("8")?.Name);
        Assert.Equal("FNMH", api.GetAccountName("8"));
        Assert.Equal("Default", api.GetSubAccountName("3"));
    }

    [Fact]
    public void Plan_reportu_se_uklada_pres_AddEditSchedule()
    {
        App.OutValues["AddEditSchedule#1"] = 0;

        CreateApi().UpsertSchedule(new ScheduleDto("7", "Denní report", "8", 1, 2, 3, true, false, false));

        var schedule = (FakeComDispatch)_com.Create(_options.ScheduleProgId);
        Assert.Equal("Denní report", schedule.Properties["ScheduleName"]);
        Assert.Equal(2, schedule.Properties["ScheduleFrequency"]);
        Assert.Equal(true, schedule.Properties["SchedulePrintReport"]);
    }

    [Fact]
    public void Sablona_reportu_se_uklada_pres_AddEditTemplate()
    {
        App.OutValues["AddEditTemplate#1"] = 0;

        CreateApi().UpsertTemplate(new TemplateDto("9", "Průchody", "8", 1, "<template/>"));

        var template = (FakeComDispatch)_com.Create(_options.TemplateProgId);
        Assert.Equal("Průchody", template.Properties["ScheduleName"]);
        Assert.Equal("<template/>", template.Properties["TemplateString"]);
    }
}
