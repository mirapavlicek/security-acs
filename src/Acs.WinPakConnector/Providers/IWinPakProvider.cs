using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers;

/// <summary>
/// Abstrakce nad zdrojem dat WIN-PAK. Implementace:
/// <list type="bullet">
///   <item><see cref="MockWinPakProvider"/> — testovací data pro vývoj.</item>
///   <item><see cref="MssqlWinPakProvider"/> — read-only čtení přímo z WIN-PAK MSSQL databáze.</item>
///   <item><see cref="SdkWinPakProvider"/> — oficiální WIN-PAK SDK (SRVWPPAPI), včetně zápisu.</item>
/// </list>
/// </summary>
public interface IWinPakProvider
{
    /// <summary>Název režimu pro diagnostiku (Mock / Mssql / Sdk).</summary>
    string Mode { get; }

    /// <summary>Zda provider umí zapisovat do WIN-PAK (přiřazení/odebrání access level).</summary>
    bool SupportsWrite { get; }

    Task<IReadOnlyList<ReaderDto>> GetReadersAsync(CancellationToken ct);

    Task<IReadOnlyList<AccessLevelDto>> GetAccessLevelsAsync(CancellationToken ct);

    Task<IReadOnlyList<CardHolderDto>> SearchCardHoldersAsync(string? search, CancellationToken ct);

    Task<CardHolderDto?> GetCardHolderAsync(string id, CancellationToken ct);

    /// <exception cref="NotSupportedException">Provider nepodporuje zápis.</exception>
    Task AssignAccessLevelAsync(string cardHolderId, string accessLevelId, CancellationToken ct);

    /// <exception cref="NotSupportedException">Provider nepodporuje zápis.</exception>
    Task RevokeAccessLevelAsync(string cardHolderId, string accessLevelId, CancellationToken ct);
}
