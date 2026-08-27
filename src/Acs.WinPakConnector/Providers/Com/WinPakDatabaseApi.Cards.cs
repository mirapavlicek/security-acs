using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers.Com;

/// <summary>Karty — čtení, zápis a hromadné operace nad rozsahem čísel.</summary>
public sealed partial class WinPakDatabaseApi
{
    public CardDto? GetCard(string cardNumber)
    {
        var result = Call("GetCardbyCardNumber", cardNumber, _options.AccountName, _options.SubAccountName, null);
        var raw = ComValue.AsEnumerable(result[3]).FirstOrDefault();
        return raw is null ? null : MapCard(_com.Wrap(raw));
    }

    /// <summary>Všechny karty účtu (<c>GetCardsByAccountName</c>).</summary>
    public IReadOnlyList<CardDto> GetCards()
        => CallList("GetCardsByAccountName", MapCard, _options.AccountName, _options.SubAccountName, null);

    /// <summary>Karty, které zatím nemají držitele (<c>GetCardsWithoutCHIDByAcctID</c>).</summary>
    public IReadOnlyList<CardDto> GetCardsWithoutHolder()
    {
        var (accountId, subAccountId) = ResolveAccountIds();
        return CallList("GetCardsWithoutCHIDByAcctID", MapCard, accountId, subAccountId, null);
    }

    public IReadOnlyList<CardDto> GetCardsByCardHolder(string cardHolderId)
        => CallList("GetCardsByCHID", MapCard, ComValue.ToLong(cardHolderId), null);

    private CardDto MapCard(IComDispatch card) => new(
        CardNumber: ComValue.ToStringOrEmpty(card.GetProperty("CardNumber")),
        RecordId: ComValue.ToStringOrNull(card.GetProperty("CardID")),
        CardHolderId: ComValue.ToStringOrNull(card.GetProperty("CardHolderID")),
        Status: (CardStatus)ComValue.ToInt(card.GetProperty("CardStatus")),
        Issue: ComValue.ToInt(card.GetProperty("Issue")),
        ActivationDate: ComValue.ToDate(card.GetProperty("ActivationDate")),
        ExpirationDate: ComValue.ToDate(card.GetProperty("ExpirationDate")),
        AccessLevelIds: ComValue.AsEnumerable(card.GetProperty("AccessLevels"))
            .Select(ComValue.ToStringOrEmpty)
            .Where(id => id.Length > 0)
            .ToList());

    /// <summary>
    /// Založí nebo upraví kartu. WIN-PAK na to má jediné volání <c>AddUpdateCard</c>
    /// (<c>dwRecordID = 0</c> znamená novou kartu).
    /// </summary>
    public void UpsertCard(string cardNumber, UpsertCardRequest request, long accountId, long subAccountId)
    {
        var existing = GetCard(cardNumber);
        var accessLevelIds = ToIds(request.AccessLevelIds ?? existing?.AccessLevelIds);

        Call("AddUpdateCard",
            ComValue.ToLong(existing?.RecordId),                                  // dwRecordID (0 = nová karta)
            cardNumber,                                                           // sCardNo
            accountId,                                                            // lAccountID
            subAccountId,                                                         // lSubAccountID
            (int)request.Status,                                                  // lCardStatus
            request.Issue,                                                        // lissue
            ComValue.ToLong(request.CardHolderId ?? existing?.CardHolderId),      // lCardHolderID
            request.Pin ?? "",                                                    // Pin1
            request.ActivationDate ?? existing?.ActivationDate ?? DateTime.Today, // dtActivationDate
            request.ExpirationDate ?? existing?.ExpirationDate ?? NoExpiration,   // dtExpirationDate
            0,                                                                    // Backdrop1ID
            0,                                                                    // Backdrop2ID
            accessLevelIds.Length > 1,                                            // bMultiple
            accessLevelIds);                                                      // alAccessLevelIDs
    }

    public void DeleteCard(string cardNumber)
        => CallCardWrite("Zrušení karty", "DeleteCard",
            cardNumber, _options.AccountName, _options.SubAccountName, 0);

    /// <summary>Založí celý rozsah karet najednou (<c>BulkAddCards</c>).</summary>
    public void BulkAddCards(BulkAddCardsRequest request)
    {
        var (accountId, subAccountId) = ResolveAccountIds();
        var accessLevelIds = ToIds(request.AccessLevelIds);
        var (operatorId, operatorName) = GetCurrentOperator();

        Call("BulkAddCards",
            request.StartNumber,
            request.StopNumber,
            accountId,
            subAccountId,
            (int)request.Status,
            request.ActivationDate ?? DateTime.Today,
            request.ExpirationDate ?? NoExpiration,
            operatorId,
            operatorName,
            accessLevelIds.Length > 1,
            accessLevelIds);
    }

    public void BulkDeleteCards(BulkDeleteCardsRequest request)
    {
        var (accountId, subAccountId) = ResolveAccountIds();
        var (operatorId, operatorName) = GetCurrentOperator();

        Call("BulkDeleteCards",
            request.StartNumber, request.StopNumber, accountId, subAccountId, operatorId, operatorName);
    }

    /// <summary>Maximální povolená délka čísla karty v této instalaci.</summary>
    public int GetMaxCardNumberLength() => ComValue.ToInt(Call("GetMaxCardNumberLength", 0)[0]);

    /// <summary>Zda instalace používá číselné karty (jinak jsou alfanumerické).</summary>
    public bool GetCardNumeric() => ComValue.ToBool(Call("GetCardNumeric", false)[0]);
}
