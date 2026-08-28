using Acs.WinPakConnector.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Acs.WinPakConnector.Providers;

/// <summary>
/// Read-only provider čtoucí přímo z WIN-PAK MSSQL databáze.
/// Schéma WIN-PAK databáze je proprietární (pod NDA) a liší se mezi verzemi,
/// proto jsou všechny SQL dotazy konfigurovatelné v appsettings
/// (<see cref="MssqlProviderOptions"/>) a výchozí hodnoty je nutné ověřit
/// proti konkrétní instalaci. Zápis tento provider záměrně nepodporuje —
/// zápis do WIN-PAK smí jít pouze přes oficiální COM API (režim Com).
/// </summary>
public sealed class MssqlWinPakProvider(IOptions<MssqlProviderOptions> options) : WinPakProviderBase
{
    private readonly MssqlProviderOptions _options = options.Value;

    public override string Mode => "Mssql";

    public override async Task<ConnectorStatusDto> GetStatusAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(ct);
            return new ConnectorStatusDto(true, [], null);
        }
        catch (Exception ex)
        {
            return new ConnectorStatusDto(false, [], ex.Message);
        }
    }

    public override async Task<IReadOnlyList<ReaderDto>> GetReadersAsync(CancellationToken ct)
    {
        var result = new List<ReaderDto>();
        await foreach (var reader in QueryAsync(_options.ReadersQuery, ct))
        {
            result.Add(new ReaderDto(
                Id: GetString(reader, "Id") ?? "",
                Name: GetString(reader, "Name") ?? "",
                Description: GetString(reader, "Description"),
                PanelName: GetString(reader, "PanelName"),
                AccountName: GetString(reader, "AccountName"),
                IsActive: GetBool(reader, "IsActive") ?? true));
        }

        return result;
    }

    public override async Task<IReadOnlyList<AccessLevelDto>> GetAccessLevelsAsync(CancellationToken ct)
    {
        var result = new List<AccessLevelDto>();
        await foreach (var reader in QueryAsync(_options.AccessLevelsQuery, ct))
        {
            result.Add(new AccessLevelDto(
                Id: GetString(reader, "Id") ?? "",
                Name: GetString(reader, "Name") ?? "",
                Description: GetString(reader, "Description")));
        }

        return result;
    }

    public override async Task<IReadOnlyList<CardHolderDto>> SearchCardHoldersAsync(string? search, CancellationToken ct)
    {
        var result = new List<CardHolderDto>();
        await foreach (var reader in QueryAsync(
            _options.CardHoldersQuery, ct,
            new SqlParameter("@search", $"%{search ?? ""}%")))
        {
            result.Add(MapCardHolder(reader));
        }

        return result;
    }

    public override async Task<CardHolderDto?> GetCardHolderAsync(string id, CancellationToken ct)
    {
        await foreach (var reader in QueryAsync(
            _options.CardHolderByIdQuery, ct,
            new SqlParameter("@id", id)))
        {
            return MapCardHolder(reader);
        }

        return null;
    }

    private static CardHolderDto MapCardHolder(SqlDataReader reader) => new(
        Id: GetString(reader, "Id") ?? "",
        FirstName: GetString(reader, "FirstName") ?? "",
        LastName: GetString(reader, "LastName") ?? "",
        Note: GetString(reader, "Note"),
        Cards: [],            // karty a access levels se dotahují samostatnými dotazy
        AccessLevelIds: []);  // dle skutečného schématu — doplní se při napojení na reálnou DB

    private async IAsyncEnumerable<SqlDataReader> QueryAsync(
        string sql,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct,
        params SqlParameter[] parameters)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new InvalidOperationException("SQL dotaz není nakonfigurován (WinPak:Mssql v appsettings).");

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return reader;
    }

    private static string? GetString(SqlDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetValue(i).ToString();
    }

    private static bool? GetBool(SqlDataReader r, string column)
    {
        int i;
        try
        {
            i = r.GetOrdinal(column);
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }

        return r.IsDBNull(i) ? null : Convert.ToBoolean(r.GetValue(i));
    }
}

/// <summary>Konfigurace MSSQL provideru (sekce <c>WinPak:Mssql</c>).</summary>
public sealed class MssqlProviderOptions
{
    public const string SectionName = "WinPak:Mssql";

    public string ConnectionString { get; set; } = "";

    /// <summary>
    /// Dotaz musí vracet sloupce: Id, Name, Description, PanelName, AccountName, IsActive.
    /// Výchozí hodnota je šablona — názvy tabulek OVĚŘTE proti vaší verzi WIN-PAK.
    /// </summary>
    public string ReadersQuery { get; set; } = "";

    /// <summary>Dotaz musí vracet sloupce: Id, Name, Description.</summary>
    public string AccessLevelsQuery { get; set; } = "";

    /// <summary>Dotaz s parametrem @search; sloupce: Id, FirstName, LastName, Note.</summary>
    public string CardHoldersQuery { get; set; } = "";

    /// <summary>Dotaz s parametrem @id; sloupce: Id, FirstName, LastName, Note.</summary>
    public string CardHolderByIdQuery { get; set; } = "";
}
