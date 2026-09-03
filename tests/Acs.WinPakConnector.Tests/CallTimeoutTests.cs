using System.Runtime.InteropServices;
using Acs.WinPakConnector.Providers.Com;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acs.WinPakConnector.Tests;

/// <summary>
/// Na ostrém serveru jedno volání, které WIN-PAK nedokončil, drželo zámek providera
/// napořád a každý další dotaz za ním visel („hledá a hledá“) až do restartu služby.
/// Provider má proto časový limit: uvázlé volání opustí, relaci zahodí a další
/// volání se přihlásí znovu.
/// </summary>
public sealed class CallTimeoutTests
{
    private readonly FakeComFactory _com = new();

    private FakeComDispatch App => (FakeComDispatch)_com.Create("NCIHelper.Application");

    private ComWinPakProvider CreateProvider()
    {
        App.OutValues["Login#3"] = 42;
        App.OutValues["ConnectWPDatabase#3"] = 0;
        App.OutValues["GetReadersByAccountName#1"] = Array.Empty<object>();
        App.OutValues["GetADVDetailsByAccountName#1"] = Array.Empty<object>();
        return new ComWinPakProvider(
            Options.Create(new WinPakComOptions
            {
                UserName = "svc", Password = "x", AccountName = "FN Motol", CallTimeoutSeconds = 1,
            }), _com);
    }

    [Fact]
    public async Task Uvazle_volani_se_po_limitu_opusti_a_dalsi_volani_projde_s_novou_relaci()
    {
        var provider = CreateProvider();
        using var stuck = new ManualResetEventSlim(false);
        App.Blocks["GetCardbyCardNumber"] = stuck;

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => provider.GetCardAsync("26372", CancellationToken.None));
        Assert.Contains("GetCardAsync", ex.Message);
        Assert.Contains("1 s", ex.Message);
        Assert.Equal(1, provider.AbandonedCalls);
        Assert.Null(provider.InFlight);

        // Druhý dotaz nečeká za prvním: projde do sekundy a přihlásí se znovu.
        var second = provider.GetReadersAsync(CancellationToken.None);
        Assert.Same(second, await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.Empty(await second);
        Assert.Equal(2, _com.Calls.Count(c => c.Method == "Login"));

        // Až uvázlé volání doběhne, zámek se neuvolní podruhé a provider dál funguje.
        stuck.Set();
        await Task.Delay(100);
        Assert.Empty(await provider.GetReadersAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Po_chybe_volani_se_relace_zahodi_stary_objekt_uvolni_a_dalsi_volani_se_prihlasi_znovu()
    {
        // Na ostrém po chybě volání viselo všechno další a pomohl jen restart služby —
        // tedy nový objekt. Provider ho založí sám.
        var provider = CreateProvider();
        App.Throws["GetCardbyCardNumber"] = new ComCallException("GetCardbyCardNumber", new COMException("Type mismatch.", unchecked((int)0x80020005)));

        var error = await Assert.ThrowsAsync<ComCallException>(() => provider.GetCardAsync("26372", CancellationToken.None));
        Assert.Contains("GetCardbyCardNumber", error.Message);
        Assert.Equal(1, provider.RecycledSessions);

        Assert.Empty(await provider.GetReadersAsync(CancellationToken.None));

        await Task.Delay(100);
        Assert.Equal(2, _com.Calls.Count(c => c.Method == "Login"));
        Assert.Contains(_com.Calls, c => c.Method == "<release>");
        // Uvolnění starého objektu nesmí být před tím, než se zahodí; nový Login je po zahození.
        Assert.Null(provider.InFlight);
    }

    [Fact]
    public async Task Volani_v_limitu_prochazi_beze_zmeny_a_nic_neopousti()
    {
        var provider = CreateProvider();

        Assert.Empty(await provider.GetReadersAsync(CancellationToken.None));

        Assert.Equal(0, provider.AbandonedCalls);
        Assert.Null(provider.InFlight);
        Assert.Equal(1, _com.Calls.Count(c => c.Method == "Login"));
    }
}
