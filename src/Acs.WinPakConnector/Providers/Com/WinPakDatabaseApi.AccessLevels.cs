using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers.Com;

/// <summary>Přístupové úrovně — čtení, zakládání, konfigurace čteček a přeřazení karet.</summary>
public sealed partial class WinPakDatabaseApi
{
    public IReadOnlyList<AccessLevelDto> GetAccessLevels()
    {
        // Bez jednoznačného účtu (více účtů, žádný nevybraný) se vrátí úrovně všech účtů.
        if (AccountNameOrNull is null)
            return CallList("GetAllAccessLevels", MapAccessLevel, [null]);

        var byAccount = CallList("GetAccessLevelsByAccountName", MapAccessLevel, AccountName, SubAccountName, null);
        if (byAccount.Count > 0)
            return byAccount;

        // Proti skutečnému WIN-PAKu vrátil dotaz za účet nic, zatímco dotaz za všechny
        // 55 úrovní — úrovně tam nejsou vázané na účet/podúčet. Číselník k mapování
        // čteček raději širší než prázdný.
        return CallList("GetAllAccessLevels", MapAccessLevel, [null]);
    }

    private static AccessLevelDto MapAccessLevel(IComDispatch level) => new(
        ComValue.ToStringOrEmpty(level.GetProperty("AccessLevelID")),
        ComValue.ToStringOrEmpty(level.GetProperty("AccessLevelName")),
        ComValue.ToStringOrNull(level.GetProperty("AccessLevelDesc")));

    public AccessLevelDto? GetAccessLevelByName(string name)
    {
        var result = Call("GetAccessLevelByName", name, AccountName, null);
        var raw = ComValue.AsEnumerable(result[2]).FirstOrDefault();
        return raw is null ? null : MapAccessLevel(_com.Wrap(raw));
    }

    public string? GetAccessLevelName(string accessLevelId)
        => ComValue.ToStringOrNull(Call("GetAccessLevelNameByID", ComValue.ToLong(accessLevelId), null)[1]);

    /// <summary>Typ přístupových úrovní v instalaci (číselník WIN-PAK).</summary>
    public int GetAccessLevelType() => ComValue.ToInt(Call("GetAccessLevelType", 0)[0]);

    /// <summary>Strom přístupů úrovně včetně časových zón, jak ho vrací <c>GetAccessTreeByName</c>.</summary>
    public string? GetAccessTree(string accessLevelName)
        => ComValue.ToStringOrNull(Call("GetAccessTreeByName", accessLevelName, AccountName, null)[2]);

    /// <summary>Založí prázdnou přístupovou úroveň (<c>CreateAccessLevel</c>).</summary>
    public void CreateAccessLevel(CreateAccessLevelRequest request)
    {
        var accountIds = request.AccountIds is { Count: > 0 }
            ? ToIds(request.AccountIds)
            : [AccountId];

        CallCardWrite("Založení přístupové úrovně", "CreateAccessLevel",
            request.Name, request.Description ?? "", accountIds, AccountName, 0);
    }

    /// <summary>Přiřadí úrovni čtečky se společnou časovou zónou (<c>ConfigureAccessLevel</c>).</summary>
    public void ConfigureAccessLevel(string accessLevelName, ConfigureAccessLevelRequest request)
        => CallCardWrite("Konfigurace přístupové úrovně", "ConfigureAccessLevel",
            accessLevelName,
            AccountName,
            request.ReaderNames.Select(object? (r) => r).ToArray(),
            request.TimeZoneName,
            0);

    /// <summary>Nastaví jednu čtečku úrovně včetně skupiny (<c>ConfigureEntranceAccess</c>).</summary>
    public void ConfigureEntranceAccess(string accessLevelName, ConfigureEntranceRequest request)
        => CallCardWrite("Konfigurace vstupu přístupové úrovně", "ConfigureEntranceAccess",
            accessLevelName,
            AccountName,
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

    /// <summary>Objektová varianta zápisu úrovně (<c>AddAccessLevel</c> / <c>EditAccessLevel</c>).</summary>
    public void AddAccessLevel(CreateAccessLevelRequest request)
    {
        EnsureSession();
        var level = CreateAccessLevelObject(request);

        var args = new object?[] { level.Target, 0 };
        App.Invoke("AddAccessLevel", args);
        WinPakStatus.EnsureCardSucceeded("Založení přístupové úrovně", ComValue.ToInt(args[1]));
    }

    public void EditAccessLevel(string currentName, CreateAccessLevelRequest request)
    {
        EnsureSession();
        var level = CreateAccessLevelObject(request);

        var args = new object?[] { currentName, level.Target, 0 };
        App.Invoke("EditAccessLevel", args);
        WinPakStatus.EnsureCardSucceeded("Úprava přístupové úrovně", ComValue.ToInt(args[2]));
    }

    private IComDispatch CreateAccessLevelObject(CreateAccessLevelRequest request)
    {
        var level = _com.Create(_options.AccessLevelProgId);
        level.SetProperty("AccessLevelName", request.Name);
        level.SetProperty("AccessLevelDesc", request.Description ?? "");
        level.SetProperty("AccountName", AccountName);
        return level;
    }

    public void DeleteAccessLevel(string accessLevelName)
        => CallCardWrite("Smazání přístupové úrovně", "DeleteAccessLevel",
            accessLevelName, AccountName, 0);

    /// <summary>Smaže úroveň a na kartách ji nahradí jinou (<c>DeleteAL</c>).</summary>
    public void DeleteAccessLevelWithReplacement(string accessLevelId, string replacementAccessLevelId, bool multiple)
        => Call("DeleteAL", ComValue.ToLong(accessLevelId), ComValue.ToLong(replacementAccessLevelId), multiple ? 1 : 0); // bMultiple As Long

    /// <summary>Karty, které úroveň používají — nutné zjistit před jejím zrušením.</summary>
    public IReadOnlyList<CardDto> IsolateAccessLevel(string accessLevelName)
    {
        var result = Call("IsolateAccessLevel", accessLevelName, AccountName, null, 0);
        WinPakStatus.EnsureCardSucceeded("Vyhledání karet přístupové úrovně", ComValue.ToInt(result[3]));
        return ComValue.AsEnumerable(result[2]).Select(_com.Wrap).Select(MapCard).ToList();
    }

    /// <summary>Úrovně, na které lze karty přeřadit (<c>GetAccesslevelsForReassign</c>).</summary>
    public IReadOnlyList<AccessLevelDto> GetAccessLevelsForReassign(string currentAccessLevelName)
        => CallList("GetAccesslevelsForReassign", MapAccessLevel,
            AccountName, currentAccessLevelName, null);

    /// <summary>Přeřadí karty ze staré úrovně na novou (<c>ReassignAccessLevel</c>).</summary>
    public void ReassignAccessLevel(string currentAccessLevelName, ReassignAccessLevelRequest request)
    {
        var cards = IsolateAccessLevel(currentAccessLevelName);
        CallCardWrite("Přeřazení karet na jinou přístupovou úroveň", "ReassignAccessLevel",
            AccountName,
            currentAccessLevelName,
            request.NewAccessLevelName,
            cards.Select(object? (c) => ComValue.ToLong(c.RecordId)).ToArray(),
            0);
    }
}
