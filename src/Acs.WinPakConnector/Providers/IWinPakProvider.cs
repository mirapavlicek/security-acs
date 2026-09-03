using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers;

/// <summary>
/// Abstrakce nad zdrojem dat WIN-PAK. Implementace:
/// <list type="bullet">
///   <item><see cref="MockWinPakProvider"/> — testovací data pro vývoj bez WIN-PAKu.</item>
///   <item><see cref="MssqlWinPakProvider"/> — read-only čtení přímo z WIN-PAK MSSQL databáze.</item>
///   <item><see cref="Com.ComWinPakProvider"/> — oficiální COM API (NCIHelper + ACCW), včetně zápisu a ovládání dveří.</item>
/// </list>
/// Metody, které provider neumí, vyhazují <see cref="NotSupportedException"/> —
/// konektor je mapuje na HTTP 501, aby ACS poznalo rozdíl mezi „nejde“ a „selhalo“.
/// </summary>
/// <summary>
/// Ukončení relace providera (odhlášení z WIN-PAKu, uvolnění zámku). Záměrně
/// není <see cref="IDisposable"/>: provider se z DI vydává jako transient přes
/// factory a ASP.NET Core by disposable instanci na konci každého požadavku
/// sám zlikvidoval — přitom ji drží <c>WinPakProviderCache</c> jako singleton.
/// Přesně to se stalo: po prvním požadavku padalo vše na „Cannot access
/// a disposed object: SemaphoreSlim“. Životní cyklus řídí jen cache.
/// </summary>
public interface IProviderShutdown
{
    void Shutdown();
}

public interface IWinPakProvider
{
    /// <summary>Název režimu pro diagnostiku (Mock / Mssql / Com).</summary>
    string Mode { get; }

    /// <summary>Zda provider umí zapisovat do WIN-PAK (karty, držitelé, přístupové úrovně).</summary>
    bool SupportsWrite { get; }

    /// <summary>Zda provider umí ovládat dveře přes komunikační server.</summary>
    bool SupportsDoorControl { get; }

    /// <summary>Účet WIN-PAK, se kterým provider pracuje (null = napříč účty).</summary>
    string? AccountName { get; }

    Task<ConnectorStatusDto> GetStatusAsync(CancellationToken ct);

    Task<IReadOnlyList<AccountDto>> GetAccountsAsync(CancellationToken ct);

    Task<IReadOnlyList<ReaderDto>> GetReadersAsync(CancellationToken ct);

    Task<IReadOnlyList<AccessLevelDto>> GetAccessLevelsAsync(CancellationToken ct);

    Task<IReadOnlyList<CardHolderDto>> SearchCardHoldersAsync(string? search, CancellationToken ct);

    Task<CardHolderDto?> GetCardHolderAsync(string id, CancellationToken ct);

    /// <summary>Založí držitele a vrátí jeho id přidělené WIN-PAKem.</summary>
    Task<string> AddCardHolderAsync(UpsertCardHolderRequest request, CancellationToken ct);

    Task EditCardHolderAsync(string id, UpsertCardHolderRequest request, CancellationToken ct);

    Task<CardDto?> GetCardAsync(string cardNumber, CancellationToken ct);

    /// <summary>Založí kartu, nebo upraví existující (WIN-PAK to řeší jedním voláním).</summary>
    Task UpsertCardAsync(string cardNumber, UpsertCardRequest request, CancellationToken ct);

    Task DeleteCardAsync(string cardNumber, CancellationToken ct);

    /// <summary>Přidá přístupovou úroveň všem aktivním kartám držitele.</summary>
    Task AssignAccessLevelAsync(string cardHolderId, string accessLevelId, CancellationToken ct);

    /// <summary>Odebere přístupovou úroveň ze všech karet držitele.</summary>
    Task RevokeAccessLevelAsync(string cardHolderId, string accessLevelId, CancellationToken ct);

    Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(CancellationToken ct);

    Task<DoorStatusDto> GetDoorStatusAsync(long hid, CancellationToken ct);

    Task PulseDoorAsync(long hid, int? seconds, CancellationToken ct);

    Task LockDoorAsync(long hid, CancellationToken ct);

    Task UnlockDoorAsync(long hid, CancellationToken ct);

    Task SetDoorModeAsync(long hid, DoorMode mode, CancellationToken ct);
}

/// <summary>Výchozí implementace, která hlásí „neumím“ — providery přepisují jen to, co zvládnou.</summary>
public abstract class WinPakProviderBase : IWinPakProvider
{
    public abstract string Mode { get; }
    public virtual bool SupportsWrite => false;
    public virtual bool SupportsDoorControl => false;
    public virtual string? AccountName => null;

    protected NotSupportedException NotSupported(string what) =>
        new($"Režim {Mode} nepodporuje {what}. Pro plnou funkčnost použijte režim Com " +
            "(oficiální WIN-PAK API přes COM na WIN-PAK serveru).");

    public abstract Task<ConnectorStatusDto> GetStatusAsync(CancellationToken ct);

    public virtual Task<IReadOnlyList<AccountDto>> GetAccountsAsync(CancellationToken ct)
        => throw NotSupported("čtení účtů");

    public abstract Task<IReadOnlyList<ReaderDto>> GetReadersAsync(CancellationToken ct);

    public abstract Task<IReadOnlyList<AccessLevelDto>> GetAccessLevelsAsync(CancellationToken ct);

    public abstract Task<IReadOnlyList<CardHolderDto>> SearchCardHoldersAsync(string? search, CancellationToken ct);

    public abstract Task<CardHolderDto?> GetCardHolderAsync(string id, CancellationToken ct);

    public virtual Task<string> AddCardHolderAsync(UpsertCardHolderRequest request, CancellationToken ct)
        => throw NotSupported("zakládání držitelů karet");

    public virtual Task EditCardHolderAsync(string id, UpsertCardHolderRequest request, CancellationToken ct)
        => throw NotSupported("úpravu držitelů karet");

    public virtual Task<CardDto?> GetCardAsync(string cardNumber, CancellationToken ct)
        => throw NotSupported("čtení karty podle čísla");

    public virtual Task UpsertCardAsync(string cardNumber, UpsertCardRequest request, CancellationToken ct)
        => throw NotSupported("zápis karet");

    public virtual Task DeleteCardAsync(string cardNumber, CancellationToken ct)
        => throw NotSupported("rušení karet");

    public virtual Task AssignAccessLevelAsync(string cardHolderId, string accessLevelId, CancellationToken ct)
        => throw NotSupported("přiřazení přístupové úrovně");

    public virtual Task RevokeAccessLevelAsync(string cardHolderId, string accessLevelId, CancellationToken ct)
        => throw NotSupported("odebrání přístupové úrovně");

    public virtual Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(CancellationToken ct)
        => throw NotSupported("výpis připojených zařízení");

    public virtual Task<DoorStatusDto> GetDoorStatusAsync(long hid, CancellationToken ct)
        => throw NotSupported("čtení stavu dveří");

    public virtual Task PulseDoorAsync(long hid, int? seconds, CancellationToken ct)
        => throw NotSupported("otevření dveří");

    public virtual Task LockDoorAsync(long hid, CancellationToken ct)
        => throw NotSupported("zamknutí dveří");

    public virtual Task UnlockDoorAsync(long hid, CancellationToken ct)
        => throw NotSupported("odemknutí dveří");

    public virtual Task SetDoorModeAsync(long hid, DoorMode mode, CancellationToken ct)
        => throw NotSupported("změnu režimu dveří");
}
