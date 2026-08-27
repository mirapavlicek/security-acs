using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers.Com;

/// <summary>
/// Přeřazení časových zón. WIN-PAK nedovolí zónu smazat, dokud ji někdo používá;
/// postup je vždy stejný: nejdřív „isolate“ (zjisti, kdo ji používá), pak
/// „reassign“ (přepiš je na novou zónu) a teprve potom smazání.
/// </summary>
public sealed partial class WinPakDatabaseApi
{
    /// <summary>Co všechno danou časovou zónu používá — souhrn pro rozhodnutí o náhradě.</summary>
    public TimeZoneUsageDto GetTimeZoneUsage(string timeZoneId)
    {
        var id = ComValue.ToLong(timeZoneId);
        return new TimeZoneUsageDto(
            TimeZoneId: timeZoneId,
            Operators: IsolateIds("IsolateOperatorsForTZReassign", "operátorů", id, includeAccount: false),
            Panels: IsolateIds("IsolatePanelsForTZDelete", "panelů", id),
            AccessLevels: IsolateIds("IsolateAccessLevelsForTZReassign", "přístupových úrovní", id),
            ActionGroups: IsolateIds("IsolateActionGroupsForTZReassign", "akčních skupin", id),
            Cards: IsolateIds("IsolateCardsForTZReassign", "karet", id),
            Devices: IsolateIds("IsolateADVsForTZReassign", "zařízení", id));
    }

    /// <summary>
    /// Isolate metody mají shodný tvar: (účet,) id zóny, <c>[out]</c> kolekce a <c>[out]</c> stav.
    /// Vracíme jen identifikátory — konektor s nimi dál pracuje jako s neprůhlednými id.
    /// </summary>
    private IReadOnlyList<string> IsolateIds(string method, string what, long timeZoneId, bool includeAccount = true)
    {
        var args = includeAccount
            ? new object?[] { AccountId, timeZoneId, null, 0 }
            : [timeZoneId, null, 0];

        var result = Call(method, args);
        WinPakStatus.EnsureCardSucceeded($"Vyhledání {what} používajících časovou zónu", ComValue.ToInt(result[^1]));

        return ComValue.AsEnumerable(result[^2])
            .Select(item => ComValue.ToStringOrNull(_com.Wrap(item).GetProperty("TimeZoneID"))
                            ?? ComValue.ToStringOrEmpty(item))
            .Where(id => id.Length > 0)
            .ToList();
    }

    /// <summary>Časové zóny, na které lze přeřadit (varianta pro operátory je bez účtu).</summary>
    public IReadOnlyList<TimeZoneDto> GetTimeZonesForReassign(string currentTimeZoneId, bool forOperators)
        => forOperators
            ? CallList("GetTZsForOperatorReassign", MapTimeZone, ComValue.ToLong(currentTimeZoneId), null)
            : CallList("GetTZsForReassign", MapTimeZone, AccountId, ComValue.ToLong(currentTimeZoneId), null);

    /// <summary>Přeřadí všechny uvedené entity ze staré časové zóny na novou.</summary>
    public void ReassignTimeZone(ReassignTimeZoneRequest request)
    {
        var oldId = ComValue.ToLong(request.CurrentTimeZoneId);
        var newId = ComValue.ToLong(request.NewTimeZoneId);

        Reassign("ReassignOperatorTZ", "operátorů", request.OperatorIds, newId, oldId, includeAccount: false, includeOldId: false);
        Reassign("ReassignAccessLevelTZ", "přístupových úrovní", request.AccessLevelIds, newId, oldId);
        Reassign("ReassignActionGroupTZ", "akčních skupin", request.ActionGroupIds, newId, oldId);
        Reassign("ReassignCardTZ", "karet", request.CardIds, newId, oldId);
        Reassign("ReassignADVTZ", "zařízení", request.DeviceIds, newId, oldId);
    }

    private void Reassign(string method, string what, IReadOnlyList<string>? ids,
        long newTimeZoneId, long oldTimeZoneId, bool includeAccount = true, bool includeOldId = true)
    {
        if (ids is not { Count: > 0 })
            return;

        // ReassignOperatorTZ má kratší tvar (bez účtu a bez staré zóny), ostatní shodný.
        var args = includeAccount
            ? includeOldId
                ? new object?[] { AccountId, oldTimeZoneId, newTimeZoneId, ToIds(ids), 0 }
                : [AccountId, newTimeZoneId, ToIds(ids), 0]
            : [newTimeZoneId, ToIds(ids), 0];

        var result = Call(method, args);
        WinPakStatus.EnsureCardSucceeded($"Přeřazení {what} na jinou časovou zónu", ComValue.ToInt(result[^1]));
    }

    /// <summary>Odebere časovou zónu z uvedených panelů (<c>DeletePanelTZ</c>).</summary>
    public void DeletePanelTimeZone(string timeZoneId, IReadOnlyList<string> panelIds)
        => CallCardWrite("Odebrání časové zóny z panelů", "DeletePanelTZ",
            AccountId, ComValue.ToLong(timeZoneId), ToIds(panelIds), 0);

    /// <summary>Souhrn časových zón smyčky účtu (<c>LoopTimeZoneByAccountId</c>).</summary>
    public string? GetLoopTimeZones()
        => ComValue.ToStringOrNull(Call("LoopTimeZoneByAccountId", AccountId, null)[1]);
}
