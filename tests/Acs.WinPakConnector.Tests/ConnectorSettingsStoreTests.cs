using Acs.WinPakConnector.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Acs.WinPakConnector.Tests;

/// <summary>Ukládání nastavení konektoru do <c>appsettings.Local.json</c>.</summary>
public sealed class ConnectorSettingsStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("winpak-settings-").FullName;
    private readonly IConfigurationRoot _configuration;
    private readonly ConnectorSettingsStore _store;

    public ConnectorSettingsStoreTests()
    {
        _configuration = new ConfigurationBuilder()
            .SetBasePath(_root)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKey"] = "puvodni-klic-0123456789",
                ["WinPak:Mode"] = "Mock",
                ["WinPak:Com:AccountName"] = "FNMH",
            })
            .AddJsonFile(ConnectorSettingsStore.FileName, optional: true, reloadOnChange: false)
            .Build();

        _store = new ConnectorSettingsStore(_configuration, new FakeEnvironment(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeEnvironment(string root) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.PhysicalFileProvider(root);
        public string EnvironmentName { get; set; } = "Test";
    }

    [Fact]
    public void Aktualni_nastaveni_se_sklada_ze_vsech_vrstev_konfigurace()
    {
        var settings = _store.Current();

        Assert.Equal("Mock", settings.Mode);
        Assert.Equal("puvodni-klic-0123456789", settings.ApiKey);
        Assert.Equal("FNMH", settings.Com.AccountName);
    }

    [Fact]
    public void Ulozene_nastaveni_se_zapise_a_znovu_nacte()
    {
        var settings = _store.Current();
        settings.Mode = "Com";
        settings.Com.UserName = "acs-service";
        settings.Com.EnableCommunicationServer = true;

        _store.Save(settings);
        _configuration.Reload();

        Assert.True(File.Exists(_store.FilePath));
        var reloaded = _store.Current();
        Assert.Equal("Com", reloaded.Mode);
        Assert.Equal("acs-service", reloaded.Com.UserName);
        Assert.True(reloaded.Com.EnableCommunicationServer);
    }

    [Fact]
    public void Prazdna_tajna_hodnota_nechá_puvodni_beze_zmeny()
    {
        var settings = _store.Current();
        settings.Com.Password = "tajne-heslo";
        _store.Save(settings);
        _configuration.Reload();

        // Druhé uložení bez hesla nesmí heslo smazat.
        var second = _store.Current();
        second.Com.Password = "";
        second.ApiKey = "";
        _store.Save(second);
        _configuration.Reload();

        var reloaded = _store.Current();
        Assert.Equal("tajne-heslo", reloaded.Com.Password);
        Assert.Equal("puvodni-klic-0123456789", reloaded.ApiKey);
    }

    [Fact]
    public void Otisk_se_zmeni_se_zmenou_rezimu_i_prihlasovacich_udaju()
    {
        var original = _store.Fingerprint();

        var settings = _store.Current();
        settings.Mode = "Mssql";
        _store.Save(settings);
        _configuration.Reload();
        var afterMode = _store.Fingerprint();

        settings = _store.Current();
        settings.Com.UserName = "jiny-operator";
        _store.Save(settings);
        _configuration.Reload();

        Assert.NotEqual(original, afterMode);
        Assert.NotEqual(afterMode, _store.Fingerprint());
    }

    [Fact]
    public void Otisk_neresi_API_klic_protoze_ten_provider_neovlivnuje()
    {
        var original = _store.Fingerprint();

        var settings = _store.Current();
        settings.ApiKey = ConnectorSettingsStore.GenerateApiKey();
        _store.Save(settings);
        _configuration.Reload();

        Assert.Equal(original, _store.Fingerprint());
    }

    [Fact]
    public void Vygenerovany_klic_ma_64_hex_znaku()
    {
        var key = ConnectorSettingsStore.GenerateApiKey();

        Assert.Equal(64, key.Length);
        Assert.Matches("^[0-9A-F]+$", key);
    }

    [Theory]
    [InlineData("", "není nastaveno")]
    [InlineData("kratke", "••••••")]
    [InlineData("abcd12345678wxyz", "abcd••••••••wxyz")]
    public void Maska_nikdy_neukaze_cely_tajny_udaj(string secret, string expected)
        => Assert.Equal(expected, ConnectorSettingsStore.Mask(secret));

    [Fact]
    public void Validace_hlida_klic_i_udaje_podle_rezimu()
    {
        var settings = new ConnectorSettings { Mode = "Com", ApiKey = "" };

        var problems = settings.Validate().Select(p => p.ErrorMessage ?? "").ToList();

        Assert.Contains(problems, p => p.Contains("API klíč"));
        Assert.Contains(problems, p => p.Contains("operátora WIN-PAK"));
        Assert.Contains(problems, p => p.Contains("heslo operátora"));
    }

    [Fact]
    public void Rezim_Mssql_vyzaduje_connection_string()
    {
        var settings = new ConnectorSettings
        {
            Mode = "Mssql",
            ApiKey = ConnectorSettingsStore.GenerateApiKey(),
        };

        Assert.Contains(settings.Validate(), p => p.ErrorMessage!.Contains("connection string"));
    }

    [Fact]
    public void Spravne_vyplnene_nastaveni_projde_bez_vyhrad()
    {
        var settings = new ConnectorSettings
        {
            Mode = "Com",
            ApiKey = ConnectorSettingsStore.GenerateApiKey(),
        };
        settings.Com.UserName = "acs-service";
        settings.Com.Password = "tajne";

        Assert.Empty(settings.Validate());
    }
}
