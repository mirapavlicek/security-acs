using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Acs.WinPakConnector.Providers;
using Acs.WinPakConnector.Providers.Com;

namespace Acs.WinPakConnector.Configuration;

/// <summary>
/// Čte a ukládá nastavení konektoru. Editovatelné hodnoty se zapisují do
/// <c>appsettings.Local.json</c> vedle programu — původní <c>appsettings.json</c>
/// z instalace zůstává nedotčený a slouží jako výchozí vrstva.
///
/// Soubor je v konfiguraci registrovaný s <c>reloadOnChange</c>, takže se změna
/// projeví bez restartu služby; provider se přestaví podle
/// <see cref="WinPakProviderCache"/>.
/// </summary>
public sealed class ConnectorSettingsStore(IConfiguration configuration, IHostEnvironment environment)
{
    public const string FileName = "appsettings.Local.json";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string FilePath => Path.Combine(environment.ContentRootPath, FileName);

    /// <summary>Aktuálně platné nastavení (výchozí soubor + lokální změny + proměnné prostředí).</summary>
    public ConnectorSettings Current()
    {
        var settings = new ConnectorSettings
        {
            Mode = configuration["WinPak:Mode"] ?? ProviderModes.Mock,
            ApiKey = configuration["Security:ApiKey"] ?? "",
            AdminPassword = configuration["Security:AdminPassword"] ?? "",
        };

        configuration.GetSection(WinPakComOptions.SectionName).Bind(settings.Com);
        configuration.GetSection(MssqlProviderOptions.SectionName).Bind(settings.Mssql);
        return settings;
    }

    /// <summary>
    /// Otisk nastavení, podle kterého se pozná, že se má provider přestavět.
    /// Počítá se ze všech hodnot, které ovlivňují chování providera.
    /// </summary>
    public string Fingerprint()
    {
        var settings = Current();
        var json = JsonSerializer.Serialize(new
        {
            settings.Mode,
            settings.Com,
            settings.Mssql,
        });

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>Zapíše nastavení do lokálního souboru. Prázdné tajné hodnoty se nepřepisují.</summary>
    public void Save(ConnectorSettings settings)
    {
        var current = Current();

        settings.ApiKey = KeepIfBlank(settings.ApiKey, current.ApiKey);
        settings.AdminPassword = KeepIfBlank(settings.AdminPassword, current.AdminPassword);
        settings.Com.Password = KeepIfBlank(settings.Com.Password, current.Com.Password);
        settings.Mssql.ConnectionString = KeepIfBlank(settings.Mssql.ConnectionString, current.Mssql.ConnectionString);

        var root = new JsonObject
        {
            ["Security"] = new JsonObject
            {
                ["ApiKey"] = settings.ApiKey,
                ["AdminPassword"] = settings.AdminPassword,
            },
            ["WinPak"] = new JsonObject
            {
                ["Mode"] = settings.Mode,
                ["Com"] = JsonSerializer.SerializeToNode(settings.Com),
                ["Mssql"] = JsonSerializer.SerializeToNode(settings.Mssql),
            },
        };

        // Zápis přes dočasný soubor, aby se při pádu neztratilo staré nastavení.
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(WriteOptions), Encoding.UTF8);
        File.Move(temporary, FilePath, overwrite: true);

        if (configuration is IConfigurationRoot reloadable)
            reloadable.Reload();
    }

    private static string KeepIfBlank(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    /// <summary>Náhodný API klíč (64 hex znaků).</summary>
    public static string GenerateApiKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Zobrazení tajné hodnoty v GUI — nikdy se neukazuje celá.</summary>
    public static string Mask(string? secret)
        => string.IsNullOrEmpty(secret)
            ? "není nastaveno"
            : secret.Length <= 8
                ? new string('•', secret.Length)
                : $"{secret[..4]}{new string('•', 8)}{secret[^4..]}";
}
