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

    // ---------- Podúčet ----------

    private void ArrangeSubAccounts(params string[] names)
        => App.OutValues["GetSubAccountsByAccountID#1"] = names
            .Select((name, i) => (object)_com.Record($"sub{i}", ("AccountID", (long)(i + 10)), ("AccountName", name)))
            .ToArray();

    [Fact]
    public void Jediny_poducet_se_pouzije_pro_drzitele_i_pristupove_urovne()
    {
        ArrangeLogin("FN Motol");
        ArrangeSubAccounts("FN Motol - hlavní");
        App.OutValues["GetAccessLevelsByAccountName#2"] = new object[]
        {
            _com.Record("al", ("AccessLevelID", 1L), ("AccessLevelName", "Vše")),
        };
        var api = CreateApi(accountName: null);

        api.GetCardHolders();
        api.GetAccessLevels();

        Assert.Equal(["FN Motol", "FN Motol - hlavní", null], _com.Call("GetCardHoldersByAccountName").Args);
        Assert.Equal("FN Motol - hlavní", _com.Call("GetAccessLevelsByAccountName").Args[1]);
        Assert.True(api.SubAccountNameResolvedAutomatically);
    }

    [Fact]
    public void Vice_poductu_bez_vyberu_znamena_prazdny_poducet()
    {
        ArrangeLogin("FN Motol");
        ArrangeSubAccounts("A", "B");
        var api = CreateApi(accountName: null);

        api.GetCardHolders();

        Assert.Equal("", _com.Call("GetCardHoldersByAccountName").Args[1]);
        Assert.False(api.SubAccountNameResolvedAutomatically);
    }

    [Fact]
    public void Pristupove_urovne_bez_vysledku_za_ucet_se_vezmou_za_vsechny()
    {
        // Skutečný WIN-PAK: za účet 0 úrovní, za všechny 55 — úrovně nejsou vázané na účet.
        ArrangeLogin("FN Motol");
        ArrangeSubAccounts("Hlavní");
        App.OutValues["GetAccessLevelsByAccountName#2"] = Array.Empty<object>();
        App.OutValues["GetAllAccessLevels#0"] = new object[]
        {
            _com.Record("al", ("AccessLevelID", 1L), ("AccessLevelName", "Vše")),
        };

        var levels = CreateApi(accountName: null).GetAccessLevels();

        Assert.Single(levels);
        Assert.NotNull(_com.Call("GetAllAccessLevels"));
    }

    // ---------- Poznámka držitele a DSN ----------

    [Fact]
    public void Indexovane_NoteField_se_precte_pres_prvni_index_a_vypis_drzitelu_nepada()
    {
        ArrangeLogin("FN Motol");
        ArrangeSubAccounts("Default");
        var holder = (FakeComDispatch)_com.Record("holder",
            ("CardHolderID", 1001L), ("FirstName", "Jan"), ("LastName", "Novák"), ("NoteField(1)", "IT oddělení"));
        // Skutečný WIN-PAK: NoteField bez indexu odmítne.
        holder.Throws["NoteField"] = new ComCallException("NoteField",
            new COMException("Number of parameters specified does not match the expected number.", unchecked((int)0x8002000E)));
        App.OutValues["GetCardHoldersByAccountName#2"] = new object[] { holder };
        App.OutValues["GetCardsByCHID#1"] = Array.Empty<object>();

        var holders = CreateApi(accountName: null).GetCardHolders();

        var single = Assert.Single(holders);
        Assert.Equal("Novák", single.LastName);
        Assert.Equal("IT oddělení", single.Note);
    }

    [Fact]
    public void Kdyz_poznamka_nejde_precist_ani_s_indexem_je_prazdna()
    {
        ArrangeLogin("FN Motol");
        ArrangeSubAccounts("Default");
        var holder = (FakeComDispatch)_com.Record("holder", ("CardHolderID", 1001L), ("FirstName", "Jan"), ("LastName", "Novák"));
        var failure = new ComCallException("NoteField", new COMException("nope", unchecked((int)0x8002000E)));
        holder.Throws["NoteField"] = failure;
        holder.Throws["NoteField(1)"] = failure;
        App.OutValues["GetCardHoldersByAccountName#2"] = new object[] { holder };
        App.OutValues["GetCardsByCHID#1"] = Array.Empty<object>();

        var single = Assert.Single(CreateApi(accountName: null).GetCardHolders());

        Assert.Null(single.Note);
    }

    [Theory]
    [InlineData("WINPAK", "WINPAK")]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    public void Prosty_nazev_zdroje_projde_beze_zmeny(string? raw, string? expected)
        => Assert.Equal(expected, WinPakDatabaseApi.DescribeDataSource(raw));

    [Fact]
    public void Pripojovaci_XML_z_GetWPDSN_nikdy_neprozradi_heslo()
    {
        // Přesně tvar, který vrátil skutečný WIN-PAK — včetně hesla k databázi v čitelné podobě.
        const string raw = "<ServerName>SQL01</ServerName><Database>WinPak</Database><dsn>WIN-PAK Database</dsn>"
                           + "<user>admin_wp</user><password>Tajne&Heslo@26</password>";

        var described = WinPakDatabaseApi.DescribeDataSource(raw);

        Assert.Equal("WIN-PAK Database, server SQL01, databáze WinPak", described);
        Assert.DoesNotContain("admin_wp", described);
        Assert.DoesNotContain("Tajne", described);
        Assert.DoesNotContain("password", described, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void XML_bez_znamych_prvku_se_neprozradi_vubec()
    {
        var described = WinPakDatabaseApi.DescribeDataSource("<user>admin_wp</user><password>x</password>");

        Assert.Equal("(připojovací údaje skryty)", described);
    }

    // ---------- Seznamy prostých hodnot ----------

    [Fact]
    public void Pole_vyhledavani_jako_seznam_retezcu_se_namapuje_s_poradim()
    {
        // Skutečný WIN-PAK vrací názvy polí, ne objekty — na řetězci by čtení vlastnosti selhalo.
        ArrangeLogin("FN Motol");
        ArrangeSubAccounts("Default");
        App.OutValues["GetCardHolderSearchFieldsByAccountName#2"] = new object[] { "LastName", "FirstName", "Note1" };

        var fields = CreateApi(accountName: null).GetCardHolderSearchFields();

        Assert.Equal(["LastName", "FirstName", "Note1"], fields.Select(f => f.Name));
        Assert.Equal([1, 2, 3], fields.Select(f => f.Index));
    }

    [Fact]
    public void Pole_vyhledavani_jako_objekty_se_namapuji_z_vlastnosti()
    {
        ArrangeLogin("FN Motol");
        ArrangeSubAccounts("Default");
        App.OutValues["GetCardHolderSearchFieldsByAccountName#2"] = new object[]
        {
            _com.Record("f1", ("NoteFieldName", "Oddělení"), ("FieldIndex", 7)),
        };

        var field = Assert.Single(CreateApi(accountName: null).GetCardHolderSearchFields());

        Assert.Equal("Oddělení", field.Name);
        Assert.Equal(7, field.Index);
    }

    [Fact]
    public void Sablony_poznamkovych_poli_jako_retezce_maji_prazdnou_definici()
    {
        ArrangeLogin("FN Motol");
        ArrangeSubAccounts("Default");
        App.OutValues["GetNoteFieldTemplateDetailsByAccount#2"] = new object[] { "Oddělení", "Telefon" };

        var templates = CreateApi(accountName: null).GetNoteFieldTemplates();

        Assert.Equal(2, templates.Count);
        Assert.Equal("Telefon", templates[1].Name);
        Assert.Equal(2, templates[1].Index);
        Assert.Null(templates[1].Definition);
    }

    [Fact]
    public void Pole_vyhledavani_s_jinym_nazvem_vlastnosti_se_prectou_z_varianty()
    {
        // Ostrý WIN-PAK: objekt nemá NoteFieldName („Unknown name“), název je pod jinou vlastností.
        ArrangeLogin("FN Motol");
        ArrangeSubAccounts("Default");
        var field = (FakeComDispatch)_com.Record("f1", ("FieldName", "Oddělení"), ("Index", 3));
        field.Throws["NoteFieldName"] = new ComCallException("NoteFieldName",
            new COMException("Unknown name.", unchecked((int)0x80020006)));
        field.Throws["FieldIndex"] = new ComCallException("FieldIndex",
            new COMException("Unknown name.", unchecked((int)0x80020006)));
        App.OutValues["GetCardHolderSearchFieldsByAccountName#2"] = new object[] { field };

        var mapped = Assert.Single(CreateApi(accountName: null).GetCardHolderSearchFields());

        Assert.Equal("Oddělení", mapped.Name);
        Assert.Equal(3, mapped.Index);
    }

    [Fact]
    public void Kdyz_nesedi_zadna_varianta_hlaska_vyjmenuje_co_se_zkouselo()
    {
        var target = (FakeComDispatch)_com.Record("x");
        foreach (var name in new[] { "A", "B" })
            target.Throws[name] = new ComCallException(name, new COMException("Unknown name.", unchecked((int)0x80020006)));

        var error = Assert.Throws<ComCallException>(() => ComMembers.ReadAny(target, "A", "B"));

        Assert.Contains("A, B", error.Message);
        Assert.True(ComMembers.IsUnknownName(error));
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

    [Fact]
    public void Null_ve_vystupnim_retezci_se_po_Type_mismatch_zopakuje_s_prazdnym_retezcem()
    {
        // Přesně situace z první diagnostiky: GetWPDSN(out dsn) s null → DISP_E_TYPEMISMATCH.
        var target = new StringOut();
        var dispatch = new ComDispatch(target);
        var args = new object?[] { null };

        dispatch.Invoke("GetWPDSN", args);

        Assert.Equal("WINPAK", args[0]);
        Assert.Equal(2, target.Calls);
    }

    [Fact]
    public void Kdyz_ani_opakovani_nepomuze_vyhodi_se_puvodni_chyba()
    {
        var dispatch = new ComDispatch(new AlwaysMismatch());

        var error = Assert.Throws<ComCallException>(() => dispatch.Invoke("Anything", [null]));

        Assert.Contains("HRESULT 0x80020005", error.Message);
    }

    [Fact]
    public void Long_se_posila_jako_32bitove_cislo()
    {
        // VB6 Long je VT_I4; C# long by šel jako VT_I8 a WIN-PAK by ho odmítl.
        var target = new IntParameter();
        var dispatch = new ComDispatch(target);
        var args = new object?[] { 4711L, null };

        dispatch.Invoke("GetDevNameByDeviceID", args);

        Assert.Equal(4711, target.ReceivedId);
        Assert.Equal("Panel 4711", args[1]);
    }

    [Fact]
    public void Long_mimo_rozsah_int_zustane_beze_zmeny()
    {
        var target = new LongParameter();
        var dispatch = new ComDispatch(target);

        dispatch.Invoke("Take", [1L << 40]);

        Assert.Equal(1L << 40, target.Received);
    }
#pragma warning restore CA1416

    /// <summary>Chová se jako WIN-PAK: null v ByRef String odmítne, prázdný řetězec přijme a přepíše.</summary>
    public sealed class StringOut
    {
        public int Calls;

        public void GetWPDSN(ref string? dsn)
        {
            Calls++;
            if (dsn is null)
                throw new COMException("Type mismatch.", unchecked((int)0x80020005));
            dsn = "WINPAK";
        }
    }

    public sealed class AlwaysMismatch
    {
        public void Anything(ref string? value) => throw new COMException("Type mismatch.", unchecked((int)0x80020005));
    }

    public sealed class IntParameter
    {
        public int ReceivedId;

        public void GetDevNameByDeviceID(int id, ref string? name)
        {
            ReceivedId = id;
            if (name is null)
                throw new COMException("Type mismatch.", unchecked((int)0x80020005));
            name = $"Panel {id}";
        }
    }

    public sealed class LongParameter
    {
        public long Received;

        public void Take(long value) => Received = value;
    }

    public sealed class Failing
    {
        public void Explode() => throw new COMException("Server odmítl požadavek", unchecked((int)0x80004005));

        public int Broken => throw new InvalidOperationException("vlastnost není k dispozici");
    }
}
