using System.ComponentModel.DataAnnotations;
using Acs.WinPakConnector.Providers;
using Acs.WinPakConnector.Providers.Com;

namespace Acs.WinPakConnector.Configuration;

/// <summary>
/// Kompletní nastavení konektoru tak, jak ho lze měnit z administračního GUI.
/// Odpovídá klíčům v appsettings (<c>Security:*</c> a <c>WinPak:*</c>).
/// </summary>
public sealed class ConnectorSettings
{
    /// <summary>Režim providera: Mock, Mssql nebo Com.</summary>
    public string Mode { get; set; } = ProviderModes.Mock;

    /// <summary>Sdílený klíč, kterým se ACS ověřuje proti <c>/api/*</c>.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Heslo do administračního GUI; prázdné = přihlašuje se API klíčem.</summary>
    public string AdminPassword { get; set; } = "";

    public WinPakComOptions Com { get; set; } = new();

    public MssqlProviderOptions Mssql { get; set; } = new();

    /// <summary>Kontrola, že nastavení dává smysl pro zvolený režim.</summary>
    public IReadOnlyList<ValidationResult> Validate()
    {
        var problems = new List<ValidationResult>();

        if (!ProviderModes.All.Contains(Mode, StringComparer.OrdinalIgnoreCase))
            problems.Add(new ValidationResult($"Neznámý režim „{Mode}“.", [nameof(Mode)]));

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            problems.Add(new ValidationResult(
                "Bez API klíče konektor odmítá všechny požadavky. Vygenerujte klíč a zadejte ho i v ACS.",
                [nameof(ApiKey)]));
        }
        else if (ApiKey.Length < 16)
        {
            problems.Add(new ValidationResult(
                "API klíč je příliš krátký — použijte alespoň 16 znaků (tlačítko Vygenerovat dá 64).",
                [nameof(ApiKey)]));
        }

        if (Mode.Equals(ProviderModes.Com, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(Com.UserName))
                problems.Add(new ValidationResult("Zadejte operátora WIN-PAK.", [nameof(Com.UserName)]));
            if (string.IsNullOrWhiteSpace(Com.Password))
                problems.Add(new ValidationResult("Zadejte heslo operátora WIN-PAK.", [nameof(Com.Password)]));
            if (string.IsNullOrWhiteSpace(Com.ApplicationProgId))
                problems.Add(new ValidationResult("ProgID databázového objektu nesmí být prázdné.", [nameof(Com.ApplicationProgId)]));
            if (Com.EnableCommunicationServer && string.IsNullOrWhiteSpace(Com.CommServerProgId))
                problems.Add(new ValidationResult("ProgID komunikačního serveru nesmí být prázdné.", [nameof(Com.CommServerProgId)]));
            if (Com.EventBufferSize < 1)
                problems.Add(new ValidationResult("Velikost bufferu událostí musí být alespoň 1.", [nameof(Com.EventBufferSize)]));
        }

        if (Mode.Equals(ProviderModes.Mssql, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(Mssql.ConnectionString))
        {
            problems.Add(new ValidationResult("Zadejte connection string k databázi WIN-PAK.", [nameof(Mssql.ConnectionString)]));
        }

        return problems;
    }

    public ConnectorSettings Clone() => new()
    {
        Mode = Mode,
        ApiKey = ApiKey,
        AdminPassword = AdminPassword,
        Com = new WinPakComOptions
        {
            UserName = Com.UserName,
            Password = Com.Password,
            Domain = Com.Domain,
            AccountName = Com.AccountName,
            SubAccountName = Com.SubAccountName,
            EnableCommunicationServer = Com.EnableCommunicationServer,
            CommViewType = Com.CommViewType,
            EventBufferSize = Com.EventBufferSize,
            ApplicationProgId = Com.ApplicationProgId,
            CardHolderProgId = Com.CardHolderProgId,
            CardProgId = Com.CardProgId,
            AccessLevelProgId = Com.AccessLevelProgId,
            ScheduleProgId = Com.ScheduleProgId,
            TemplateProgId = Com.TemplateProgId,
            TimeZoneProgId = Com.TimeZoneProgId,
            HolidayProgId = Com.HolidayProgId,
            HolidayGroupProgId = Com.HolidayGroupProgId,
            CommServerProgId = Com.CommServerProgId,
            NetAxsDoorInfoProgId = Com.NetAxsDoorInfoProgId,
        },
        Mssql = new MssqlProviderOptions
        {
            ConnectionString = Mssql.ConnectionString,
            ReadersQuery = Mssql.ReadersQuery,
            AccessLevelsQuery = Mssql.AccessLevelsQuery,
            CardHoldersQuery = Mssql.CardHoldersQuery,
            CardHolderByIdQuery = Mssql.CardHolderByIdQuery,
        },
    };
}

/// <summary>Povolené režimy providera na jednom místě, ať se názvy nerozejdou.</summary>
public static class ProviderModes
{
    public const string Mock = "Mock";
    public const string Mssql = "Mssql";
    public const string Com = "Com";

    public static readonly string[] All = [Mock, Mssql, Com];

    public static string Describe(string mode) => mode.ToLowerInvariant() switch
    {
        "mock" => "Ukázková data v paměti — pro vývoj a zkoušení bez WIN-PAKu.",
        "mssql" => "Read-only čtení přímo z databáze WIN-PAK; zápis a ovládání dveří nejsou dostupné.",
        "com" => "Oficiální WIN-PAK API přes COM+. Plná funkčnost včetně zápisu a ovládání dveří.",
        _ => "",
    };
}
