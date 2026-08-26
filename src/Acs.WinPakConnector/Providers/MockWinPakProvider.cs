using System.Collections.Concurrent;
using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers;

/// <summary>
/// Testovací provider s ukázkovými daty (budova → patra → místnosti → čtečky).
/// Používá se při vývoji hlavní ACS aplikace bez přístupu k WIN-PAK.
/// Zápis je podporovaný a mění data v paměti procesu.
/// </summary>
public sealed class MockWinPakProvider : IWinPakProvider
{
    public string Mode => "Mock";
    public bool SupportsWrite => true;

    private static readonly IReadOnlyList<ReaderDto> Readers =
    [
        new("R-001", "Hlavní vchod",        "Vstup do budovy A",        "PRO4200-A1", "FNMH", true),
        new("R-002", "Budova A - 1.patro",  "Schodiště/výtah 1. patro", "PRO4200-A1", "FNMH", true),
        new("R-003", "Chodba A-101",        "Chodba u serverovny",      "PRO4200-A1", "FNMH", true),
        new("R-004", "Serverovna A-105",    "Serverovna",               "PRO4200-A1", "FNMH", true),
        new("R-005", "Budova A - 2.patro",  "Schodiště/výtah 2. patro", "PRO4200-A2", "FNMH", true),
        new("R-006", "Kancelář A-201",      "Sekretariát",              "PRO4200-A2", "FNMH", true),
        new("R-007", "Sklad B-001",         "Sklad materiálu, bud. B",  "MPA2-B1",    "FNMH", true),
        new("R-008", "Lékárna B-010",       "Výdej léčiv, bud. B",      "MPA2-B1",    "FNMH", false),
    ];

    private static readonly IReadOnlyList<AccessLevelDto> AccessLevels =
    [
        new("AL-01", "Vstup budova A",  "Hlavní vchod + společné prostory"),
        new("AL-02", "Patro A1",        "1. patro budovy A"),
        new("AL-03", "Serverovna",      "Chodba A-101 + serverovna A-105"),
        new("AL-04", "Patro A2",        "2. patro budovy A"),
        new("AL-05", "Sklad B",         "Sklad materiálu v budově B"),
    ];

    private readonly ConcurrentDictionary<string, CardHolderDto> _cardHolders = new(
        new Dictionary<string, CardHolderDto>
        {
            ["CH-1001"] = new(
                "CH-1001", "Jan", "Novák", "IT oddělení",
                [new CardDto("100234", "Active", new DateTime(2024, 1, 1), null)],
                ["AL-01", "AL-02"]),
            ["CH-1002"] = new(
                "CH-1002", "Petra", "Svobodová", "Sekretariát",
                [new CardDto("100888", "Active", new DateTime(2023, 6, 1), new DateTime(2027, 6, 1))],
                ["AL-01", "AL-04"]),
            ["CH-1003"] = new(
                "CH-1003", "Karel", "Dvořák", "Údržba",
                [new CardDto("100501", "Inactive", new DateTime(2022, 3, 1), new DateTime(2026, 3, 1))],
                ["AL-01", "AL-05"]),
        });

    public Task<IReadOnlyList<ReaderDto>> GetReadersAsync(CancellationToken ct)
        => Task.FromResult(Readers);

    public Task<IReadOnlyList<AccessLevelDto>> GetAccessLevelsAsync(CancellationToken ct)
        => Task.FromResult(AccessLevels);

    public Task<IReadOnlyList<CardHolderDto>> SearchCardHoldersAsync(string? search, CancellationToken ct)
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

    public Task<CardHolderDto?> GetCardHolderAsync(string id, CancellationToken ct)
        => Task.FromResult(_cardHolders.TryGetValue(id, out var ch) ? ch : null);

    public Task AssignAccessLevelAsync(string cardHolderId, string accessLevelId, CancellationToken ct)
    {
        if (AccessLevels.All(al => al.Id != accessLevelId))
            throw new KeyNotFoundException($"Access level '{accessLevelId}' neexistuje.");

        var updated = _cardHolders.AddOrUpdate(
            cardHolderId,
            _ => throw new KeyNotFoundException($"Card holder '{cardHolderId}' neexistuje."),
            (_, ch) => ch.AccessLevelIds.Contains(accessLevelId)
                ? ch
                : ch with { AccessLevelIds = [.. ch.AccessLevelIds, accessLevelId] });
        _ = updated;
        return Task.CompletedTask;
    }

    public Task RevokeAccessLevelAsync(string cardHolderId, string accessLevelId, CancellationToken ct)
    {
        _cardHolders.AddOrUpdate(
            cardHolderId,
            _ => throw new KeyNotFoundException($"Card holder '{cardHolderId}' neexistuje."),
            (_, ch) => ch with
            {
                AccessLevelIds = ch.AccessLevelIds.Where(id => id != accessLevelId).ToList(),
            });
        return Task.CompletedTask;
    }
}
