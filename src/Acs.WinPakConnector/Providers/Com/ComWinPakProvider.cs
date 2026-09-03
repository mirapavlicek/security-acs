using Acs.WinPakConnector.Models;
using Microsoft.Extensions.Options;

namespace Acs.WinPakConnector.Providers.Com;

/// <summary>
/// Provider nad oficiálním WIN-PAK API. Databázová část jde přes COM objekt
/// <c>NCIHelper.Application</c>, ovládání dveří přes <c>ACCW.MTSCBServer</c>.
///
/// COM+ objekty drží relaci operátora a nejsou bezpečné pro souběžné volání,
/// proto se všechna volání serializují jedním semaforem. Zátěž je malá
/// (synchronizace číselníků a jednotlivé zápisy karet), takže to nevadí.
/// </summary>
public sealed partial class ComWinPakProvider : WinPakProviderBase, IProviderShutdown
{
    private readonly WinPakComOptions _options;
    private readonly WinPakDatabaseApi _database;
    private readonly WinPakCommApi? _comm;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ComWinPakProvider(IOptions<WinPakComOptions> options, IComFactory com)
    {
        _options = options.Value;
        _database = new WinPakDatabaseApi(com, _options);
        _comm = _options.EnableCommunicationServer ? new WinPakCommApi(com, _options) : null;
    }

    public override string Mode => "Com";
    public override bool SupportsWrite => true;
    public override bool SupportsDoorControl => _comm is not null;
    /// <summary>Účet nebyl v konfiguraci a databázové API si ho doplnilo samo (jediný ve WIN-PAKu).</summary>
    public bool AccountResolvedAutomatically => _database.AccountNameResolvedAutomatically;

    /// <summary>Podúčet, se kterým se pracuje (z konfigurace, nebo jediný u účtu); prázdný = žádný.</summary>
    public string EffectiveSubAccountName => _database.IsLoggedIn ? _database.SubAccountName : "";

    public bool SubAccountResolvedAutomatically => _database.SubAccountNameResolvedAutomatically;

    /// <summary>Účet z konfigurace, nebo ten, který si databázové API doplnilo samo (jediný ve WIN-PAKu).</summary>
    public override string? AccountName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_options.AccountName))
                return _options.AccountName;

            try
            {
                return _database.IsLoggedIn ? _database.AccountName : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public void Shutdown()
    {
        _comm?.Close();
        _database.Close();
        _gate.Dispose();
    }

    /// <summary>
    /// Krátká paměť pro číselníky, které se nemění minutu od minuty (čtečky, úrovně,
    /// účty, zóny, panely). Stránky i diagnostika si je tahají opakovaně a každé
    /// načtení je řada COM roundtripů; karty a držitelé se necachují, ty ACS mění.
    /// Běží vždy pod zámkem <see cref="_gate"/>, takže bez další synchronizace.
    /// </summary>
    private static readonly TimeSpan CatalogTtl = TimeSpan.FromSeconds(60);
    private readonly Dictionary<string, (DateTime Until, object Value)> _catalogCache = [];

    private T Cached<T>(string key, Func<T> load) where T : class
    {
        if (_catalogCache.TryGetValue(key, out var entry) && entry.Until > DateTime.UtcNow)
            return (T)entry.Value;

        var value = load();
        _catalogCache[key] = (DateTime.UtcNow + CatalogTtl, value);
        return value;
    }

    /// <summary>Po zápisu, který může číselník změnit, se paměť zahodí.</summary>
    internal void InvalidateCatalogCache() => _catalogCache.Clear();

    private async Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return work();
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task RunAsync(Action work, CancellationToken ct)
        => RunAsync(() => { work(); return true; }, ct);

    /// <summary>Zápis do číselníku: po provedení se zahodí cache, aby další čtení vidělo změnu.</summary>
    private Task RunAsyncInvalidating(Action work, CancellationToken ct)
        => RunAsync(() => { work(); InvalidateCatalogCache(); return true; }, ct);

    private Task<T> RunAsyncInvalidating<T>(Func<T> work, CancellationToken ct)
        => RunAsync(() => { var result = work(); InvalidateCatalogCache(); return result; }, ct);

    private WinPakCommApi Comm => _comm ?? throw NotSupported("ovládání dveří (vypnuté v konfiguraci konektoru)");

    public override Task<ConnectorStatusDto> GetStatusAsync(CancellationToken ct)
        => RunAsync(() =>
        {
            try
            {
                _database.EnsureSession();
                var servers = _comm is null ? [] : _comm.GetServerStatus();
                return new ConnectorStatusDto(_database.IsConnected(), servers, null);
            }
            catch (Exception ex)
            {
                return new ConnectorStatusDto(false, [], ex.Message);
            }
        }, ct);

    public override Task<IReadOnlyList<AccountDto>> GetAccountsAsync(CancellationToken ct)
        => RunAsync(() => Cached("accounts", _database.GetAccounts), ct);

    public override Task<IReadOnlyList<ReaderDto>> GetReadersAsync(CancellationToken ct)
        => RunAsync(() => Cached("readers", _database.GetReaders), ct);

    public override Task<IReadOnlyList<AccessLevelDto>> GetAccessLevelsAsync(CancellationToken ct)
        => RunAsync(() => Cached("access-levels", _database.GetAccessLevels), ct);

    public override Task<IReadOnlyList<CardHolderDto>> SearchCardHoldersAsync(string? search, CancellationToken ct)
        => RunAsync<IReadOnlyList<CardHolderDto>>(() =>
        {
            var holders = _database.GetCardHolders();
            return string.IsNullOrWhiteSpace(search) ? holders : Filter(holders, search);
        }, ct);

    /// <summary>WIN-PAK filtruje jen přes samostatné vyhledávací API, které vyžaduje konfiguraci polí — filtrujeme v konektoru.</summary>
    internal static IReadOnlyList<CardHolderDto> Filter(IEnumerable<CardHolderDto> holders, string search)
        => holders.Where(h =>
                h.FirstName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || h.LastName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || h.Cards.Any(c => c.CardNumber.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    public override Task<CardHolderDto?> GetCardHolderAsync(string id, CancellationToken ct)
        => RunAsync(() => _database.GetCardHolder(id), ct);

    public override Task<string> AddCardHolderAsync(UpsertCardHolderRequest request, CancellationToken ct)
        => RunAsync(() => _database.AddCardHolder(request), ct);

    public override Task EditCardHolderAsync(string id, UpsertCardHolderRequest request, CancellationToken ct)
        => RunAsync(() => _database.EditCardHolder(id, request), ct);

    public override Task<CardDto?> GetCardAsync(string cardNumber, CancellationToken ct)
        => RunAsync(() => _database.GetCard(cardNumber), ct);

    public override Task UpsertCardAsync(string cardNumber, UpsertCardRequest request, CancellationToken ct)
        => RunAsync(() =>
        {
            var (accountId, subAccountId) = _database.ResolveAccountIds();
            _database.UpsertCard(cardNumber, request, accountId, subAccountId);
        }, ct);

    public override Task DeleteCardAsync(string cardNumber, CancellationToken ct)
        => RunAsync(() => _database.DeleteCard(cardNumber), ct);

    public override Task AssignAccessLevelAsync(string cardHolderId, string accessLevelId, CancellationToken ct)
        => RunAsync(() => ChangeAccessLevel(cardHolderId, accessLevelId, grant: true), ct);

    public override Task RevokeAccessLevelAsync(string cardHolderId, string accessLevelId, CancellationToken ct)
        => RunAsync(() => ChangeAccessLevel(cardHolderId, accessLevelId, grant: false), ct);

    /// <summary>
    /// Oprávnění nese ve WIN-PAKu karta, ne držitel. Změna se proto promítá
    /// do všech jeho karet: načtou se, přepočítá se seznam úrovní a uloží zpět.
    /// </summary>
    private void ChangeAccessLevel(string cardHolderId, string accessLevelId, bool grant)
    {
        var cards = _database.GetCardsByCardHolder(cardHolderId);
        if (cards.Count == 0)
            throw new KeyNotFoundException(
                $"Držitel karty '{cardHolderId}' nemá ve WIN-PAK žádnou kartu — přístup není kam zapsat.");

        var (accountId, subAccountId) = _database.ResolveAccountIds();
        foreach (var card in cards)
        {
            // Zrušené karty se needitují, jen aktivní a sledované.
            if (grant && card.Status is CardStatus.LostOrStolen)
                continue;

            var levels = RecalculateAccessLevels(card.AccessLevelIds, accessLevelId, grant);
            if (levels.SequenceEqual(card.AccessLevelIds))
                continue;

            _database.UpsertCard(card.CardNumber, new UpsertCardRequest(
                CardHolderId: card.CardHolderId ?? cardHolderId,
                Status: card.Status,
                Issue: card.Issue,
                ActivationDate: card.ActivationDate,
                ExpirationDate: card.ExpirationDate,
                Pin: null,
                AccessLevelIds: levels), accountId, subAccountId);
        }
    }

    /// <summary>Přidá nebo odebere úroveň bez duplicit a se zachováním pořadí ostatních.</summary>
    internal static IReadOnlyList<string> RecalculateAccessLevels(
        IReadOnlyList<string> current, string accessLevelId, bool grant)
    {
        if (!grant)
            return current.Where(id => id != accessLevelId).ToList();

        return current.Contains(accessLevelId) ? current : [.. current, accessLevelId];
    }

    public override Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(CancellationToken ct)
        => RunAsync(Comm.ListConnectedDevices, ct);

    public override Task<DoorStatusDto> GetDoorStatusAsync(long hid, CancellationToken ct)
        => RunAsync(() => Comm.GetDoorStatus(hid), ct);

    public override Task PulseDoorAsync(long hid, int? seconds, CancellationToken ct)
        => RunAsync(() => Comm.Pulse(hid, seconds), ct);

    public override Task LockDoorAsync(long hid, CancellationToken ct)
        => RunAsync(() => Comm.LockDoor(hid), ct);

    public override Task UnlockDoorAsync(long hid, CancellationToken ct)
        => RunAsync(() => Comm.UnlockDoor(hid), ct);

    public override Task SetDoorModeAsync(long hid, DoorMode mode, CancellationToken ct)
        => RunAsync(() => Comm.SetDoorMode(hid, mode), ct);

    /// <summary>Poslední události z panelů (jen v režimu s komunikačním serverem).</summary>
    public Task<IReadOnlyList<WinPakEvent>> GetRecentEventsAsync(int limit, CancellationToken ct)
        => RunAsync(() =>
        {
            Comm.EnsureStarted();
            return Comm.GetRecentEvents(limit);
        }, ct);
}
