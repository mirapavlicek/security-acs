using Acs.WinPakConnector.Providers;
using Acs.WinPakConnector.Providers.Com;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acs.WinPakConnector.Tests;

/// <summary>
/// Provider se z DI vydává jako transient přes factory a přitom ho drží cache jako
/// singleton. ASP.NET Core každou <see cref="IDisposable"/> instanci vytvořenou
/// factory zlikviduje na konci scope (požadavku) — kdyby provider byl disposable,
/// druhý požadavek by padl na „Cannot access a disposed object: SemaphoreSlim“.
/// Přesně to se stalo na ostrém konektoru.
/// </summary>
public sealed class ProviderLifetimeTests
{
    private static ComWinPakProvider CreateComProvider(FakeComFactory com)
    {
        var app = (FakeComDispatch)com.Create("NCIHelper.Application");
        app.OutValues["Login#3"] = 42;
        app.OutValues["ConnectWPDatabase#3"] = 0;
        app.OutValues["GetAccounts#0"] = new object[] { com.Record("acc", ("AccountID", 1L), ("AccountName", "FN Motol")) };
        app.OutValues["GetSubAccountsByAccountID#1"] = Array.Empty<object>();

        return new ComWinPakProvider(
            Options.Create(new WinPakComOptions { UserName = "svc", Password = "x", AccountName = "FN Motol" }),
            com);
    }

    [Fact]
    public void Provider_neni_IDisposable_aby_ho_DI_nelikvidovalo()
        => Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(ComWinPakProvider)));

    [Fact]
    public async Task Provider_prezije_konec_scope_jako_na_ostrem_serveru()
    {
        var provider = CreateComProvider(new FakeComFactory());
        var services = new ServiceCollection();
        // Stejná registrace jako v Program.cs: transient přes factory, instance držená mimo DI.
        services.AddTransient<IWinPakProvider>(_ => provider);
        await using var root = services.BuildServiceProvider();

        // Požadavek 1: vyzvednout provider ze scope a scope ukončit.
        using (var scope = root.CreateScope())
        {
            var resolved = scope.ServiceProvider.GetRequiredService<IWinPakProvider>();
            Assert.Single(await resolved.GetAccountsAsync(CancellationToken.None));
        }

        // Požadavek 2: tentýž provider musí dál fungovat.
        using (var scope = root.CreateScope())
        {
            var resolved = scope.ServiceProvider.GetRequiredService<IWinPakProvider>();
            Assert.Single(await resolved.GetAccountsAsync(CancellationToken.None));
        }
    }

    // ---------- Obnova po pádu COM+ serveru ----------

    [Fact]
    public void Po_padu_RPC_se_relace_obnovi_a_volani_zopakuje()
    {
        var com = new FakeComFactory();
        var app = (FakeComDispatch)com.Create("NCIHelper.Application");
        app.OutValues["Login#3"] = 42;
        app.OutValues["ConnectWPDatabase#3"] = 0;
        app.OutValues["GetAccounts#0"] = new object[] { com.Record("acc", ("AccountID", 1L), ("AccountName", "FN Motol")) };
        app.OutValues["GetSubAccountsByAccountID#1"] = Array.Empty<object>();
        // Přesně hlášky z ostrého serveru: nejdřív spadlé RPC, další volání „server unavailable“.
        app.ThrowsOnce["GetAccounts"] = new Queue<Exception>([
            new ComCallException("GetAccounts", new System.Runtime.InteropServices.COMException(
                "The remote procedure call failed.", unchecked((int)0x800706BE))),
        ]);
        var api = new WinPakDatabaseApi(com, new WinPakComOptions { UserName = "svc", Password = "x", AccountName = "FN Motol" });

        var accounts = api.GetAccounts();

        Assert.Single(accounts);
        // Přihlášení proběhlo dvakrát: původní relace a nová po pádu.
        Assert.Equal(2, com.Calls.Count(c => c.Method == "Login"));
        Assert.Equal(2, com.Calls.Count(c => c.Method == "GetAccounts"));
    }

    [Fact]
    public void Jina_chyba_nez_ztrata_spojeni_relaci_neobnovuje()
    {
        var com = new FakeComFactory();
        var app = (FakeComDispatch)com.Create("NCIHelper.Application");
        app.OutValues["Login#3"] = 42;
        app.OutValues["ConnectWPDatabase#3"] = 0;
        app.Throws["GetAccounts"] = new ComCallException("GetAccounts", new System.Runtime.InteropServices.COMException(
            "Unknown name.", unchecked((int)0x80020006)));
        var api = new WinPakDatabaseApi(com, new WinPakComOptions { UserName = "svc", Password = "x", AccountName = "FN Motol" });

        var error = Assert.Throws<ComCallException>(() => api.GetAccounts());

        Assert.False(error.IsConnectionLost);
        Assert.Equal(1, com.Calls.Count(c => c.Method == "Login"));
    }

    [Fact]
    public void Neznamy_clen_nese_v_hlasce_sve_jmeno()
    {
        // Reflexe vyhodí MissingMethodException bez obalu — dřív tak z hlášky
        // „Unknown name“ nešlo poznat, který člen WIN-PAK nezná.
#pragma warning disable CA1416
        var dispatch = new ComDispatch(new object());
        var error = Assert.Throws<ComCallException>(() => dispatch.Invoke("GetCardbyCardNumber", []));
#pragma warning restore CA1416

        Assert.Equal("GetCardbyCardNumber", error.Member);
        Assert.Contains("GetCardbyCardNumber", error.Message);
    }

    [Fact]
    public async Task Po_Shutdown_z_cache_provider_odmitne_dalsi_volani_srozumitelne()
    {
        var provider = CreateComProvider(new FakeComFactory());
        await provider.GetAccountsAsync(CancellationToken.None);

        ((IProviderShutdown)provider).Shutdown();

        // Zlikvidovaný zámek — po přestavení cache se takový provider už nikomu nevydá,
        // test jen dokládá, že Shutdown relaci skutečně uzavře.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.GetAccountsAsync(CancellationToken.None));
    }
}
