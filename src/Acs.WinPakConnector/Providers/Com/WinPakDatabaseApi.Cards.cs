using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers.Com;

/// <summary>Karty — čtení, zápis a hromadné operace nad rozsahem čísel.</summary>
public sealed partial class WinPakDatabaseApi
{
    public CardDto? GetCard(string cardNumber)
    {
        var result = Call("GetCardbyCardNumber", cardNumber, AccountName, SubAccountName, null);
        var raw = ComValue.AsEnumerable(result[3]).FirstOrDefault();
        return raw is null ? null : MapCard(_com.Wrap(raw));
    }

    /// <summary>Všechny karty účtu (<c>GetCardsByAccountName</c>).</summary>
    public IReadOnlyList<CardDto> GetCards()
        => CallList("GetCardsByAccountName", MapCard, AccountName, SubAccountName, null);

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

        try
        {
            AddUpdateCard(cardNumber, request, existing, accessLevelIds, accountId, subAccountId);
        }
        catch (ComCallException ex) when (existing is not null && ex.IsConnectionLost)
        {
            // AddUpdateCard na ostrém shodilo COM+ proces (i po obnovení relace). Existující
            // kartu jde upravit i cestou, kterou používá sám WIN-PAK: vzít jeho objekt karty,
            // změnit pole a poslat ho zpět přes EditCard — bez 18 argumentů, ve kterých se dá
            // trefit něco, co komponenta nepřežije.
            EditCardViaObject(cardNumber, request, accessLevelIds);
        }
    }

    private void AddUpdateCard(string cardNumber, UpsertCardRequest request, CardDto? existing, int[] accessLevelIds,
        long accountId, long subAccountId)
    {
        // Skutečná signatura (typová informace ostrého WIN-PAKu): 18 parametrů, žádný
        // výstupní stav — za 14 z příručky následují čtyři NetAXS volby, bMultiple je Long.
        var status = CallWithResult("AddUpdateCard",
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
            Multiple(accessLevelIds),                                             // bMultiple As Long
            accessLevelIds,                                                       // alAccessLevelIDs
            false,                                                                // bTempCard
            (short)0,                                                             // iNXCardType
            (short)0,                                                             // nUsageLimits
            false);                                                               // bLimitedCard
        EnsureWriteSucceeded($"Uložení karty {cardNumber}", status);
    }

    /// <summary>
    /// Úprava existující karty přes objekt karty z WIN-PAKu: <c>GetCardbyCardNumber</c>
    /// vrátí skutečný <c>ICard</c> se všemi poli, změní se jen to, co žádost mění,
    /// a objekt se vrátí přes <c>EditCard(cardNo, account, subAccount, ByRef card, ByRef status)</c>.
    /// </summary>
    internal void EditCardViaObject(string cardNumber, UpsertCardRequest request, int[] accessLevelIds)
    {
        var result = Call("GetCardbyCardNumber", cardNumber, AccountName, SubAccountName, null);
        var raw = ComValue.AsEnumerable(result[3]).FirstOrDefault()
                  ?? throw new KeyNotFoundException($"Karta {cardNumber} ve WIN-PAKu není.");
        var card = _com.Wrap(raw);

        card.SetProperty("CardStatus", (int)request.Status);
        card.SetProperty("Issue", request.Issue);
        if (request.CardHolderId is not null)
            card.SetProperty("CardHolderID", ComValue.ToLong(request.CardHolderId));
        if (request.ActivationDate is { } activation)
            card.SetProperty("ActivationDate", activation);
        if (request.ExpirationDate is { } expiration)
            card.SetProperty("ExpirationDate", expiration);
        if (request.Pin is not null)
            card.SetProperty("PIN1", request.Pin);
        if (request.AccessLevelIds is not null)
            card.SetProperty("AccessLevels", accessLevelIds);

        var args = new object?[] { cardNumber, AccountName, SubAccountName, card.Target, 0 };
        App.Invoke("EditCard", args);
        WinPakStatus.EnsureCardSucceeded($"Uložení karty {cardNumber} (přes objekt karty)", ComValue.ToInt(args[4]));
    }

    /// <summary>WIN-PAK chce <c>bMultiple As Long</c>: 1 = více přístupových úrovní, 0 = jedna nebo žádná.</summary>
    private static int Multiple(int[] accessLevelIds) => accessLevelIds.Length > 1 ? 1 : 0;

    /// <summary>Stavový kód zápisu, pokud ho WIN-PAK vrátil (návratovou hodnotou nebo doplněným výstupním parametrem).</summary>
    private static void EnsureWriteSucceeded(string operation, object? status)
    {
        if (status is int or short or long or byte or uint)
            WinPakStatus.EnsureCardSucceeded(operation, ComValue.ToInt(status));
    }

    public void DeleteCard(string cardNumber)
        => CallCardWrite("Zrušení karty", "DeleteCard",
            cardNumber, AccountName, SubAccountName, 0);

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
            Multiple(accessLevelIds),
            accessLevelIds);
    }

    public void BulkDeleteCards(BulkDeleteCardsRequest request)
    {
        var (accountId, subAccountId) = ResolveAccountIds();
        var (operatorId, operatorName) = GetCurrentOperator();

        Call("BulkDeleteCards",
            request.StartNumber, request.StopNumber, accountId, subAccountId, operatorId, operatorName);
    }

    /// <summary>
    /// Starší objektová varianta zápisu karty (<c>AddCard</c> / <c>EditCard</c>).
    /// <see cref="UpsertCard"/> je pro běžné použití praktičtější, ale některé
    /// instalace mají na těchto voláních navázané vlastní chování.
    /// </summary>
    public void AddCard(string cardNumber, UpsertCardRequest request)
    {
        EnsureSession();
        var card = CreateCardObject(cardNumber, request);

        var args = new object?[] { card.Target, 0 };
        App.Invoke("AddCard", args);
        WinPakStatus.EnsureCardSucceeded("Založení karty", ComValue.ToInt(args[1]));
    }

    public void EditCard(string cardNumber, UpsertCardRequest request)
    {
        EnsureSession();
        var card = CreateCardObject(cardNumber, request);

        var args = new object?[] { cardNumber, AccountName, SubAccountName, card.Target, 0 };
        App.Invoke("EditCard", args);
        WinPakStatus.EnsureCardSucceeded("Úprava karty", ComValue.ToInt(args[4]));
    }

    private IComDispatch CreateCardObject(string cardNumber, UpsertCardRequest request)
    {
        var card = _com.Create(_options.CardProgId);
        card.SetProperty("CardNumber", cardNumber);
        card.SetProperty("AccountName", AccountName);
        if (!string.IsNullOrWhiteSpace(SubAccountName))
            card.SetProperty("SubAccountName", SubAccountName);
        card.SetProperty("Issue", request.Issue);
        card.SetProperty("ActivationDate", request.ActivationDate ?? DateTime.Today);
        card.SetProperty("ExpirationDate", request.ExpirationDate ?? NoExpiration);
        if (request.CardHolderId is not null)
            card.SetProperty("CardHolderID", ComValue.ToLong(request.CardHolderId));
        if (request.Pin is not null)
            card.SetProperty("PIN1", request.Pin);
        if (request.AccessLevelIds is { Count: > 0 })
            card.SetProperty("AccessLevels", ToIds(request.AccessLevelIds));

        return card;
    }

    /// <summary>
    /// Rozšířená varianta <c>AddUpdateCardEx</c> s nastavením NetAXS karty
    /// (dočasná/omezená karta, typ, limit použití, trigger).
    /// </summary>
    public void UpsertCardEx(string cardNumber, UpsertCardRequest request, NetAxsCardOptions netAxs,
        long accountId, long subAccountId)
    {
        var existing = GetCard(cardNumber);
        var accessLevelIds = ToIds(request.AccessLevelIds ?? existing?.AccessLevelIds);

        var status = CallWithResult("AddUpdateCardEx",
            ComValue.ToLong(existing?.RecordId),
            cardNumber,
            accountId,
            subAccountId,
            (int)request.Status,
            request.Issue,
            ComValue.ToLong(request.CardHolderId ?? existing?.CardHolderId),
            request.Pin ?? "",
            request.ActivationDate ?? existing?.ActivationDate ?? DateTime.Today,
            request.ExpirationDate ?? existing?.ExpirationDate ?? NoExpiration,
            0,
            0,
            Multiple(accessLevelIds),
            accessLevelIds,
            netAxs.TemporaryCard,
            (short)netAxs.CardType,
            (short)netAxs.UsageLimit,
            netAxs.LimitedCard,
            netAxs.Trigger);
        EnsureWriteSucceeded($"Uložení karty {cardNumber} s NetAXS volbami", status);
    }

    /// <summary>Maximální povolená délka čísla karty v této instalaci.</summary>
    public int GetMaxCardNumberLength() => ComValue.ToInt(Call("GetMaxCardNumberLength", 0)[0]);

    /// <summary>Zda instalace používá číselné karty (jinak jsou alfanumerické).</summary>
    public bool GetCardNumeric() => ComValue.ToBool(Call("GetCardNumeric", false)[0]);
}
