using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers;

/// <summary>
/// Provider nad oficiálním WIN-PAK API (SRVWPPAPI — Database API + Communication API).
///
/// SDK je distribuováno Honeywellem pod NDA jako klient–server rozhraní (C++/COM)
/// a v tomto repozitáři nemůže být. Až bude licence a SDK k dispozici:
/// <list type="number">
///   <item>nainstalujte SDK na WIN-PAK server (kde běží tento konektor),</item>
///   <item>vytvořte COM interop / P-Invoke vrstvu (SDK je Windows-only),</item>
///   <item>implementujte metody níže: čtení čteček a access levels přes Database API,
///         přiřazení access level držiteli karty (AssignAccessLevelAsync) přes Database API.</item>
/// </list>
/// REST rozhraní konektoru se přitom nezmění — hlavní ACS aplikace pozná rozdíl
/// jen podle <c>SupportsWrite = true</c>.
/// </summary>
public sealed class SdkWinPakProvider : IWinPakProvider
{
    public string Mode => "Sdk";
    public bool SupportsWrite => true;

    private const string NotYetAvailable =
        "WIN-PAK SDK (SRVWPPAPI) zatím není integrováno — vyžaduje NDA a licenci od Honeywellu. " +
        "Použijte režim Mock (vývoj) nebo Mssql (read-only produkce).";

    public Task<IReadOnlyList<ReaderDto>> GetReadersAsync(CancellationToken ct)
        => throw new NotSupportedException(NotYetAvailable);

    public Task<IReadOnlyList<AccessLevelDto>> GetAccessLevelsAsync(CancellationToken ct)
        => throw new NotSupportedException(NotYetAvailable);

    public Task<IReadOnlyList<CardHolderDto>> SearchCardHoldersAsync(string? search, CancellationToken ct)
        => throw new NotSupportedException(NotYetAvailable);

    public Task<CardHolderDto?> GetCardHolderAsync(string id, CancellationToken ct)
        => throw new NotSupportedException(NotYetAvailable);

    public Task AssignAccessLevelAsync(string cardHolderId, string accessLevelId, CancellationToken ct)
        => throw new NotSupportedException(NotYetAvailable);

    public Task RevokeAccessLevelAsync(string cardHolderId, string accessLevelId, CancellationToken ct)
        => throw new NotSupportedException(NotYetAvailable);
}
