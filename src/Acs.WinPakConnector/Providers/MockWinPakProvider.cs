using System.Collections.Concurrent;
using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers;

/// <summary>
/// Testovací provider s ukázkovými daty (budova → patra → místnosti → čtečky).
/// Používá se při vývoji hlavní ACS aplikace bez přístupu k WIN-PAK.
/// Chová se jako plnohodnotný provider včetně zápisu a ovládání dveří,
/// ale všechno se odehrává v paměti procesu.
/// </summary>
public sealed partial class MockWinPakProvider : WinPakProviderBase
{
    public override string Mode => "Mock";
    public override bool SupportsWrite => true;
    public override bool SupportsDoorControl => true;
    public override string? AccountName => "FNMH";

    // Id čtečky je HWDeviceID, tedy číslo — stejně jako ve skutečném WIN-PAKu,
    // aby proti mocku fungovalo i ovládání dveří, které adresuje zařízení číselným HID.
    private static readonly IReadOnlyList<ReaderDto> Readers =
    [
        new("101", "Hlavní vchod",        "Vstup do budovy A",        "PRO4200-A1", "FNMH", true),
        new("102", "Budova A - 1.patro",  "Schodiště/výtah 1. patro", "PRO4200-A1", "FNMH", true),
        new("103", "Chodba A-101",        "Chodba u serverovny",      "PRO4200-A1", "FNMH", true),
        new("104", "Serverovna A-105",    "Serverovna",               "PRO4200-A1", "FNMH", true),
        new("105", "Budova A - 2.patro",  "Schodiště/výtah 2. patro", "PRO4200-A2", "FNMH", true),
        new("106", "Kancelář A-201",      "Sekretariát",              "PRO4200-A2", "FNMH", true),
        new("107", "Sklad B-001",         "Sklad materiálu, bud. B",  "MPA2-B1",    "FNMH", true),
        new("108", "Lékárna B-010",       "Výdej léčiv, bud. B",      "MPA2-B1",    "FNMH", false),
    ];

    private static readonly IReadOnlyList<AccessLevelDto> AccessLevels =
    [
        new("AL-01", "Vstup budova A",  "Hlavní vchod + společné prostory"),
        new("AL-02", "Patro A1",        "1. patro budovy A"),
        new("AL-03", "Serverovna",      "Chodba A-101 + serverovna A-105"),
        new("AL-04", "Patro A2",        "2. patro budovy A"),
        new("AL-05", "Sklad B",         "Sklad materiálu v budově B"),
    ];

    private static readonly IReadOnlyList<AccountDto> Accounts =
    [
        new("1", "FNMH", [new SubAccountDto("1", "Default")]),
    ];

    private readonly ConcurrentDictionary<string, CardHolderDto> _cardHolders = new(
        new Dictionary<string, CardHolderDto>
        {
            ["CH-1001"] = Holder("CH-1001", "Jan", "Novák", "IT oddělení",
                Card("100234", "CH-1001", CardStatus.Active, new DateTime(2024, 1, 1), null, ["AL-01", "AL-02"])),
            ["CH-1002"] = Holder("CH-1002", "Petra", "Svobodová", "Sekretariát",
                Card("100888", "CH-1002", CardStatus.Active, new DateTime(2023, 6, 1), new DateTime(2027, 6, 1), ["AL-01", "AL-04"])),
            ["CH-1003"] = Holder("CH-1003", "Karel", "Dvořák", "Údržba",
                Card("100501", "CH-1003", CardStatus.Inactive, new DateTime(2022, 3, 1), new DateTime(2026, 3, 1), ["AL-01", "AL-05"])),
        });

    private readonly ConcurrentDictionary<long, DoorMode> _doorModes = new();
    private int _nextCardHolderId = 2000;

    private static CardDto Card(string number, string holderId, CardStatus status,
        DateTime? from, DateTime? to, IReadOnlyList<string> levels)
        => new(number, RecordId: number, holderId, status, Issue: 0, from, to, levels);

    private static CardHolderDto Holder(string id, string first, string last, string? note, params CardDto[] cards)
        => new(id, first, last, note, cards,
            cards.SelectMany(c => c.AccessLevelIds).Distinct().ToList());

    private static CardHolderDto WithCards(CardHolderDto holder, IReadOnlyList<CardDto> cards)
        => holder with
        {
            Cards = cards,
            AccessLevelIds = cards.SelectMany(c => c.AccessLevelIds).Distinct().ToList(),
        };

    public override Task<ConnectorStatusDto> GetStatusAsync(CancellationToken ct)
        => Task.FromResult(new ConnectorStatusDto(
            DatabaseServerConnected: true,
            Servers:
            [
                new ServerStatusDto("1", "MockDatabaseServer", true, "1"),
                new ServerStatusDto("2", "MockCommServer", true, "2"),
            ],
            Error: null));

    public override Task<IReadOnlyList<AccountDto>> GetAccountsAsync(CancellationToken ct)
        => Task.FromResult(Accounts);

    public override Task<IReadOnlyList<ReaderDto>> GetReadersAsync(CancellationToken ct)
        => Task.FromResult(Readers);

    public override Task<IReadOnlyList<AccessLevelDto>> GetAccessLevelsAsync(CancellationToken ct)
        => Task.FromResult(AccessLevels);

    public override Task<IReadOnlyList<CardHolderDto>> SearchCardHoldersAsync(string? search, CancellationToken ct)
    {
        IEnumerable<CardHolderDto> result = _cardHolders.Values;
        if (!string.IsNullOrWhiteSpace(search))
        {
            result = result.Where(ch =>
                ch.FirstName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                ch.LastName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                ch.Cards.Any(c => c.CardNumber.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        return Task.FromResult<IReadOnlyList<CardHolderDto>>(
            result.OrderBy(ch => ch.LastName).ToList());
    }

    public override Task<CardHolderDto?> GetCardHolderAsync(string id, CancellationToken ct)
        => Task.FromResult(_cardHolders.TryGetValue(id, out var ch) ? ch : null);

    public override Task<string> AddCardHolderAsync(UpsertCardHolderRequest request, CancellationToken ct)
    {
        var id = $"CH-{Interlocked.Increment(ref _nextCardHolderId)}";
        _cardHolders[id] = new CardHolderDto(id, request.FirstName, request.LastName, request.Note, [], []);
        return Task.FromResult(id);
    }

    public override Task EditCardHolderAsync(string id, UpsertCardHolderRequest request, CancellationToken ct)
    {
        _cardHolders[id] = Require(id) with
        {
            FirstName = request.FirstName,
            LastName = request.LastName,
            Note = request.Note,
        };
        return Task.CompletedTask;
    }

    public override Task<CardDto?> GetCardAsync(string cardNumber, CancellationToken ct)
        => Task.FromResult(_cardHolders.Values
            .SelectMany(h => h.Cards)
            .FirstOrDefault(c => c.CardNumber == cardNumber));

    public override Task UpsertCardAsync(string cardNumber, UpsertCardRequest request, CancellationToken ct)
    {
        var holderId = request.CardHolderId
            ?? _cardHolders.Values.FirstOrDefault(h => h.Cards.Any(c => c.CardNumber == cardNumber))?.Id
            ?? throw new KeyNotFoundException($"Karta '{cardNumber}' není přiřazena držiteli.");

        var holder = Require(holderId);
        var existing = holder.Cards.FirstOrDefault(c => c.CardNumber == cardNumber);
        var card = new CardDto(
            cardNumber,
            RecordId: existing?.RecordId ?? cardNumber,
            CardHolderId: holderId,
            Status: request.Status,
            Issue: request.Issue,
            ActivationDate: request.ActivationDate ?? existing?.ActivationDate,
            ExpirationDate: request.ExpirationDate ?? existing?.ExpirationDate,
            AccessLevelIds: request.AccessLevelIds ?? existing?.AccessLevelIds ?? []);

        _cardHolders[holderId] = WithCards(holder,
            [.. holder.Cards.Where(c => c.CardNumber != cardNumber), card]);
        return Task.CompletedTask;
    }

    public override Task DeleteCardAsync(string cardNumber, CancellationToken ct)
    {
        foreach (var holder in _cardHolders.Values.Where(h => h.Cards.Any(c => c.CardNumber == cardNumber)))
            _cardHolders[holder.Id] = WithCards(holder, holder.Cards.Where(c => c.CardNumber != cardNumber).ToList());

        return Task.CompletedTask;
    }

    public override Task AssignAccessLevelAsync(string cardHolderId, string accessLevelId, CancellationToken ct)
    {
        if (AccessLevels.All(al => al.Id != accessLevelId))
            throw new KeyNotFoundException($"Access level '{accessLevelId}' neexistuje.");

        return ChangeAccessLevelAsync(cardHolderId, accessLevelId, grant: true);
    }

    public override Task RevokeAccessLevelAsync(string cardHolderId, string accessLevelId, CancellationToken ct)
        => ChangeAccessLevelAsync(cardHolderId, accessLevelId, grant: false);

    private Task ChangeAccessLevelAsync(string cardHolderId, string accessLevelId, bool grant)
    {
        var holder = Require(cardHolderId);

        // Stejně jako skutečný WIN-PAK: oprávnění se zapisuje na karty držitele.
        var cards = holder.Cards
            .Select(card => card with
            {
                AccessLevelIds = grant
                    ? card.AccessLevelIds.Contains(accessLevelId)
                        ? card.AccessLevelIds
                        : [.. card.AccessLevelIds, accessLevelId]
                    : card.AccessLevelIds.Where(id => id != accessLevelId).ToList(),
            })
            .ToList();

        _cardHolders[cardHolderId] = WithCards(holder, cards);
        return Task.CompletedTask;
    }

    public override Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<DeviceDto>>(
            Readers.Select(r => new DeviceDto(r.Id, r.Name, "Reader")).ToList());

    public override Task<DoorStatusDto> GetDoorStatusAsync(long hid, CancellationToken ct)
        => Task.FromResult(new DoorStatusDto(
            hid.ToString(), $"Mock dveře {hid}",
            IsOpen: false, IsShunted: false, ForcedOpen: false, Ajar: false,
            Account: AccountName, SubAccount: "Default"));

    public override Task PulseDoorAsync(long hid, int? seconds, CancellationToken ct) => Task.CompletedTask;

    public override Task LockDoorAsync(long hid, CancellationToken ct)
    {
        _doorModes[hid] = DoorMode.Locked;
        return Task.CompletedTask;
    }

    public override Task UnlockDoorAsync(long hid, CancellationToken ct)
    {
        _doorModes[hid] = DoorMode.Unlocked;
        return Task.CompletedTask;
    }

    public override Task SetDoorModeAsync(long hid, DoorMode mode, CancellationToken ct)
    {
        _doorModes[hid] = mode;
        return Task.CompletedTask;
    }

    /// <summary>Poslední nastavený režim dveří — pro ověření v testech.</summary>
    public DoorMode? GetDoorMode(long hid) => _doorModes.TryGetValue(hid, out var mode) ? mode : null;

    private CardHolderDto Require(string id)
        => _cardHolders.TryGetValue(id, out var holder)
            ? holder
            : throw new KeyNotFoundException($"Card holder '{id}' neexistuje.");
}
