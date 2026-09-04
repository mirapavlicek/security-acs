using Acs.WinPakConnector.Providers.Com;
using Acs.WinPakConnector.Providers.Com.Signatures;
using Xunit;

namespace Acs.WinPakConnector.Tests;

/// <summary>
/// Kontrola signatur: katalog volání konektoru (přepis příručky) proti skutečným
/// signaturám objektu WIN-PAKu. Katalog vzniká spuštěním API nad zaznamenávající
/// atrapou — tady se ověřuje, že pokrývá to, co konektor opravdu volá, a že
/// porovnání rozlišuje rozdíly, které konektor vyrovná sám, od těch, které ne.
/// </summary>
public sealed class SignatureCheckTests
{
    private static readonly WinPakComOptions Options = new()
    {
        UserName = "svc", Password = "x", AccountName = "FN Motol", EnableCommunicationServer = true,
    };

    [Fact]
    public void Katalog_pokryva_volani_databazoveho_i_komunikacniho_API()
    {
        var catalog = ConnectorCallCatalog.Record(Options);

        var methods = catalog.Calls.Select(c => c.Method).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Login", methods);
        Assert.Contains("ConnectWPDatabase", methods);
        Assert.Contains("AddUpdateCard", methods);
        Assert.Contains("GetReadersByAccountName", methods);
        Assert.Contains("GetCardHoldersByAccountName", methods);
        Assert.Contains("ConfigurePanelHolidayGroup", methods);
        Assert.Contains("InitServer", methods);
        Assert.Contains("GetDoorStatus2", methods);
        Assert.True(methods.Count >= 80, $"katalog má jen {methods.Count} metod");

        var addUpdateCard = catalog.Calls.Single(c => c.Method == "AddUpdateCard");
        Assert.Equal(18, addUpdateCard.Arguments.Count);
        Assert.Equal("Long()", addUpdateCard.Arguments[13].Type);
        Assert.Equal("WinPakDatabaseApi.UpsertCard", addUpdateCard.Origin);
    }

    private static SignatureCheck.ObjectDescription Describe(params ComMembers.ComMethodSignature[] signatures)
        => new(
            method => signatures.FirstOrDefault(s => s.Name.Equals(method, StringComparison.OrdinalIgnoreCase)),
            signatures.Select(s => s.Name).Append("SomethingElse").ToList());

    private static ComMembers.ComMethodSignature Sig(string name, ComMembers.ComParameter[] parameters, string? returnType)
        => new(name, parameters, returnType);

    private static RecordedCall Call(string method, params object?[] args)
        => new(method, args.Select(SentArgument.Of).ToList(), "test");

    [Fact]
    public void Shodna_signatura_sedi()
    {
        var result = SignatureCheck.Check(
            Call("DeleteCard", "1", "FN Motol", "Default", 0),
            Describe(Sig("DeleteCard",
            [
                new("sCardNo", "String", false, false), new("sAccount", "String", false, false),
                new("sSubAccount", "String", false, false), new("lStatus", "Long", true, false),
            ], null)));

        Assert.Equal(SignatureVerdict.Ok, result.Verdict);
    }

    [Fact]
    public void Chybejici_vystupni_parametr_na_konci_se_vyrovna_za_behu()
    {
        var result = SignatureCheck.Check(
            Call("AddUpdateCard", 0, "1"),
            Describe(Sig("AddUpdateCard",
            [
                new("dwRecordID", "Long", false, false), new("sCardNo", "String", false, false),
                new("lStatus", "Long", true, false),
            ], null)));

        Assert.Equal(SignatureVerdict.Learnable, result.Verdict);
        Assert.Contains("chybí ByRef lStatus As Long", result.Note);
    }

    [Fact]
    public void Null_misto_vystupniho_retezce_a_nula_misto_variantu_se_vyrovnaji_za_behu()
    {
        var dsn = SignatureCheck.Check(Call("GetWPDSN", new object?[] { null }),
            Describe(Sig("GetWPDSN", [new("sDSN", "String", true, false)], null)));
        var size = SignatureCheck.Check(Call("GetPhotoSize", 1, 0, 0),
            Describe(Sig("GetPhotoSize",
            [
                new("lCHID", "Long", false, false), new("lIndex", "Long", false, false), new("vSize", "Variant", true, false),
            ], null)));

        Assert.Equal(SignatureVerdict.Learnable, dsn.Verdict);
        Assert.Equal(SignatureVerdict.Learnable, size.Verdict);
    }

    [Fact]
    public void Prebyvajici_parametry_a_jiny_typ_jsou_rozdil()
    {
        var tooMany = SignatureCheck.Check(Call("AddCard", 1, "1", "x"),
            Describe(Sig("AddCard", [new("lID", "Long", false, false), new("sNo", "String", false, false)], "Long")));
        var wrongType = SignatureCheck.Check(Call("GetAccountByAcctID", "1", null),
            Describe(Sig("GetAccountByAcctID", [new("lID", "Long", false, false), new("vAccount", "Variant", true, false)], null)));

        Assert.Equal(SignatureVerdict.Mismatch, tooMany.Verdict);
        Assert.Contains("posílá 3 parametrů, WIN-PAK má 2", tooMany.Note);
        Assert.Equal(SignatureVerdict.Mismatch, wrongType.Verdict);
        Assert.Contains("1. lID: posílá se String, chce Long", wrongType.Note);
    }

    [Fact]
    public void Metoda_kterou_objekt_nema_se_oznaci_jako_chybejici()
    {
        var result = SignatureCheck.Check(Call("GetSigSize", 1, 0, 0), Describe());

        Assert.Equal(SignatureVerdict.Missing, result.Verdict);
    }

    [Fact]
    public void Volitelne_parametry_na_konci_nechybi()
    {
        var result = SignatureCheck.Check(Call("PulseByHID", 1),
            Describe(Sig("PulseByHID", [new("hid", "Long", false, false), new("seconds", "Long", false, true)], "Boolean")));

        Assert.Equal(SignatureVerdict.Ok, result.Verdict);
    }
}
