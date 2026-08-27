using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers.Com;

/// <summary>Přístupové úrovně — čtení, zakládání, konfigurace čteček a přeřazení karet.</summary>
public sealed partial class WinPakDatabaseApi
{
    public IReadOnlyList<AccessLevelDto> GetAccessLevels()
        => string.IsNullOrWhiteSpace(_options.AccountName)
            ? CallList("GetAllAccessLevels", MapAccessLevel, [null])
            : CallList("GetAccessLevelsByAccountName", MapAccessLevel,
                _options.AccountName, _options.SubAccountName, null);

    private static AccessLevelDto MapAccessLevel(IComDispatch level) => new(
        ComValue.ToStringOrEmpty(level.GetProperty("AccessLevelID")),
        ComValue.ToStringOrEmpty(level.GetProperty("AccessLevelName")),
        ComValue.ToStringOrNull(level.GetProperty("AccessLevelDesc")));

    public AccessLevelDto? GetAccessLevelByName(string name)
    {
        var result = Call("GetAccessLevelByName", name, _options.AccountName, null);
        var raw = ComValue.AsEnumerable(result[2]).FirstOrDefault();
        return raw is null ? null : MapAccessLevel(_com.Wrap(raw));
    }

    public string? GetAccessLevelName(string accessLevelId)
        => ComValue.ToStringOrNull(Call("GetAccessLevelNameByID", ComValue.ToLong(accessLevelId), null)[1]);

    /// <summary>Typ přístupových úrovní v instalaci (číselník WIN-PAK).</summary>
    public int GetAccessLevelType() => ComValue.ToInt(Call("GetAccessLevelType", 0)[0]);

    /// <summary>Strom přístupů úrovně včetně časových zón, jak ho vrací <c>GetAccessTreeByName</c>.</summary>
    public string? GetAccessTree(string accessLevelName)
        => ComValue.ToStringOrNull(Call("GetAccessTreeByName", accessLevelName, _options.AccountName, null)[2]);

    /// <summary>Založí prázdnou přístupovou úroveň (<c>CreateAccessLevel</c>).</summary>
    public void CreateAccessLevel(CreateAccessLevelRequest request)
    {
        var accountIds = request.AccountIds is { Count: > 0 }
            ? ToIds(request.AccountIds)
            : [AccountId];

        CallCardWrite("Založení přístupové úrovně", "CreateAccessLevel",
            request.Name, request.Description ?? "", accountIds, _options.AccountName, 0);
    }

    /// <summary>Přiřadí úrovni čtečky se společnou časovou zónou (<c>ConfigureAccessLevel</c>).</summary>
    public void ConfigureAccessLevel(string accessLevelName, ConfigureAccessLevelRequest request)
        => CallCardWrite("Konfigurace přístupové úrovně", "ConfigureAccessLevel",
            accessLevelName,
            _options.AccountName,
            request.ReaderNames.Select(object? (r) => r).ToArray(),
            request.TimeZoneName,
            0);

    /// <summary>Nastaví jednu čtečku úrovně včetně skupiny (<c>ConfigureEntranceAccess</c>).</summary>
    public void ConfigureEntranceAccess(string accessLevelName, ConfigureEntranceRequest request)
        => CallCardWrite("Konfigurace vstupu přístupové úrovně", "ConfigureEntranceAccess",
            accessLevelName,
            _options.AccountName,
            request.ReaderName,
            request.TimeZoneName,
            request.GroupName ?? "",
            0);

    /// <summary>Úplný zápis úrovně včetně čteček, jejich časových zón a skupin (<c>AddUpdateAL</c>).</summary>
    public void UpsertAccessLevel(string? accessLevelId, UpsertAccessLevelRequest request)
        => Call("AddUpdateAL",
            ComValue.ToLong(accessLevelId),                 // dwAccesslevelID (0 = nová)
            request.Name,                                   // sName
            request.Description ?? "",                      // sDesc
            AccountId,                                      // lAccountID
            ToIds(request.SubAccountIds),                   // anSubAccountIDs
            ToIds(request.ReaderIds),                       // anReaderIDs
            ToIds(request.ReaderTimeZoneIds),               // anReaderTimeZones
            ToIds(request.ReaderGroupIds));                 // anReaderGroups

    public void DeleteAccessLevel(string accessLevelName)
        => CallCardWrite("Smazání přístupové úrovně", "DeleteAccessLevel",
            accessLevelName, _options.AccountName, 0);

    /// <summary>Smaže úroveň a na kartách ji nahradí jinou (<c>DeleteAL</c>).</summary>
    public void DeleteAccessLevelWithReplacement(string accessLevelId, string replacementAccessLevelId, bool multiple)
        => Call("DeleteAL", ComValue.ToLong(accessLevelId), ComValue.ToLong(replacementAccessLevelId), multiple);

    /// <summary>Karty, které úroveň používají — nutné zjistit před jejím zrušením.</summary>
    public IReadOnlyList<CardDto> IsolateAccessLevel(string accessLevelName)
    {
        var result = Call("IsolateAccessLevel", accessLevelName, _options.AccountName, null, 0);
        WinPakStatus.EnsureCardSucceeded("Vyhledání karet přístupové úrovně", ComValue.ToInt(result[3]));
        return ComValue.AsEnumerable(result[2]).Select(_com.Wrap).Select(MapCard).ToList();
    }

    /// <summary>Úrovně, na které lze karty přeřadit (<c>GetAccesslevelsForReassign</c>).</summary>
    public IReadOnlyList<AccessLevelDto> GetAccessLevelsForReassign(string currentAccessLevelName)
        => CallList("GetAccesslevelsForReassign", MapAccessLevel,
            _options.AccountName, currentAccessLevelName, null);

    /// <summary>Přeřadí karty ze staré úrovně na novou (<c>ReassignAccessLevel</c>).</summary>
    public void ReassignAccessLevel(string currentAccessLevelName, ReassignAccessLevelRequest request)
    {
        var cards = IsolateAccessLevel(currentAccessLevelName);
        CallCardWrite("Přeřazení karet na jinou přístupovou úroveň", "ReassignAccessLevel",
            _options.AccountName,
            currentAccessLevelName,
            request.NewAccessLevelName,
            cards.Select(object? (c) => ComValue.ToLong(c.RecordId)).ToArray(),
            0);
    }
}
