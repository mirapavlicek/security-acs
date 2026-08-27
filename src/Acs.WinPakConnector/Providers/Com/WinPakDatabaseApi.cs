using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers.Com;

/// <summary>
/// Obálka nad Database Server API WIN-PAKu (COM objekt <c>NCIHelper.Application</c>).
/// Názvy a pořadí parametrů odpovídají příručce „WIN-PAK 4.9 Database Server API Guide“;
/// mapování je pokryté testy proti atrapě <see cref="IComDispatch"/>.
///
/// Třída není bezpečná pro souběžné volání — COM+ objekt drží relaci operátora.
/// Serializaci zajišťuje <see cref="ComWinPakProvider"/>.
/// </summary>
public sealed class WinPakDatabaseApi(IComFactory com, WinPakComOptions options)
{
    // WIN-PAK neumí kartu „bez expirace“ — místo toho se dává datum daleko v budoucnu.
    private static readonly DateTime NoExpiration = new(2099, 12, 31);

    private IComDispatch? _app;
    private int _userId;
    private readonly Dictionary<long, string?> _panelNames = [];

    public bool IsLoggedIn => _app is not null && _userId > 0;

    private IComDispatch App => _app ?? throw new InvalidOperationException("Není přihlášeno k WIN-PAK.");

    /// <summary>Přihlásí se a připojí k databázovému serveru. Opakované volání nic nedělá.</summary>
    public void EnsureSession()
    {
        if (IsLoggedIn)
            return;

        var app = _app ??= com.Create(options.ApplicationProgId);

        var login = new object?[] { options.UserName, options.Password, options.Domain, 0 };
        app.Invoke("Login", login);
        var userId = ComValue.ToInt(login[3]);
        if (userId <= 0)
        {
            _app = null;
            throw new InvalidOperationException(
                "Přihlášení k WIN-PAK se nezdařilo — ověřte uživatele, heslo a doménu v konfiguraci konektoru.");
        }

        var connect = new object?[] { options.UserName, options.Password, options.Domain, 0, userId };
        app.Invoke("ConnectWPDatabase", connect);
        var status = ComValue.ToInt(connect[3]);
        if (status == -2)
        {
            _app = null;
            throw new InvalidOperationException("Připojení k databázi WIN-PAK selhalo (status -2).");
        }

        _userId = userId;
    }

    public void Close()
    {
        if (_app is null)
            return;

        try
        {
            _app.Invoke("DisconnectWPDatabase", []);
            _app.Invoke("Logout", []);
        }
        catch (Exception)
        {
            // Ukončení relace je best-effort; spojení stejně zahazujeme.
        }

        _app = null;
        _userId = 0;
        _panelNames.Clear();
    }

    public bool IsConnected()
    {
        if (_app is null)
            return false;

        var args = new object?[] { false };
        App.Invoke("IsConnected", args);
        return ComValue.ToBool(args[0]);
    }

    // ---------- Účty ----------

    public IReadOnlyList<AccountDto> GetAccounts()
    {
        EnsureSession();
        var args = new object?[] { null };
        App.Invoke("GetAccounts", args);

        var accounts = new List<AccountDto>();
        foreach (var raw in ComValue.AsEnumerable(args[0]))
        {
            var account = com.Wrap(raw);
            var id = ComValue.ToStringOrEmpty(account.GetProperty("AccountID"));
            accounts.Add(new AccountDto(
                Id: id,
                Name: ComValue.ToStringOrEmpty(account.GetProperty("AccountName")),
                SubAccounts: GetSubAccounts(ComValue.ToLong(id))));
        }

        return accounts;
    }

    private IReadOnlyList<SubAccountDto> GetSubAccounts(long accountId)
    {
        var args = new object?[] { accountId, null };
        App.Invoke("GetSubAccountsByAccountID", args);

        return ComValue.AsEnumerable(args[1])
            .Select(com.Wrap)
            .Select(sub => new SubAccountDto(
                ComValue.ToStringOrEmpty(sub.GetProperty("AccountID")),
                ComValue.ToStringOrEmpty(sub.GetProperty("AccountName"))))
            .ToList();
    }

    // ---------- Čtečky ----------

    public IReadOnlyList<ReaderDto> GetReaders()
    {
        EnsureSession();
        var args = new object?[] { options.AccountName, null };
        App.Invoke("GetReadersByAccountName", args);

        var readers = new List<ReaderDto>();
        foreach (var raw in ComValue.AsEnumerable(args[1]))
        {
            var device = com.Wrap(raw);
            var deviceId = ComValue.ToLong(device.GetProperty("DeviceID"));
            readers.Add(new ReaderDto(
                // Comm API adresuje zařízení přes HWDeviceID — používáme ho i jako id čtečky,
                // aby šlo ze stejného identifikátoru rovnou ovládat dveře.
                Id: ComValue.ToStringOrEmpty(device.GetProperty("HWDeviceID")),
                Name: ComValue.ToStringOrEmpty(device.GetProperty("DeviceName")),
                Description: ComValue.ToStringOrNull(device.GetProperty("DeviceDesc")),
                PanelName: GetPanelName(deviceId),
                AccountName: options.AccountName,
                IsActive: true));
        }

        return readers;
    }

    /// <summary>Název panelu se dotahuje podle DeviceID a cachuje — čteček bývají stovky.</summary>
    private string? GetPanelName(long deviceId)
    {
        if (deviceId <= 0)
            return null;

        if (_panelNames.TryGetValue(deviceId, out var cached))
            return cached;

        var args = new object?[] { deviceId, null };
        App.Invoke("GetDevNameByDeviceID", args);
        var name = ComValue.ToStringOrNull(args[1]);
        _panelNames[deviceId] = name;
        return name;
    }

    // ---------- Přístupové úrovně ----------

    public IReadOnlyList<AccessLevelDto> GetAccessLevels()
    {
        EnsureSession();

        object?[] args;
        if (string.IsNullOrWhiteSpace(options.AccountName))
        {
            args = [null];
            App.Invoke("GetAllAccessLevels", args);
        }
        else
        {
            args = [options.AccountName, options.SubAccountName, null];
            App.Invoke("GetAccessLevelsByAccountName", args);
        }

        return ComValue.AsEnumerable(args[^1])
            .Select(com.Wrap)
            .Select(level => new AccessLevelDto(
                ComValue.ToStringOrEmpty(level.GetProperty("AccessLevelID")),
                ComValue.ToStringOrEmpty(level.GetProperty("AccessLevelName")),
                ComValue.ToStringOrNull(level.GetProperty("AccessLevelDesc"))))
            .ToList();
    }

    // ---------- Držitelé karet ----------

    public IReadOnlyList<CardHolderDto> GetCardHolders()
    {
        EnsureSession();
        var args = new object?[] { options.AccountName, options.SubAccountName, null };
        App.Invoke("GetCardHoldersByAccountName", args);

        return ComValue.AsEnumerable(args[2])
            .Select(com.Wrap)
            .Select(MapCardHolder)
            .ToList();
    }

    public CardHolderDto? GetCardHolder(string cardHolderId)
    {
        EnsureSession();
        var args = new object?[] { ComValue.ToLong(cardHolderId), null };
        App.Invoke("GetCardHolderByCardHolderID", args);

        var raw = ComValue.AsEnumerable(args[1]).FirstOrDefault();
        return raw is null ? null : MapCardHolder(com.Wrap(raw));
    }

    private CardHolderDto MapCardHolder(IComDispatch holder)
    {
        var id = ComValue.ToStringOrEmpty(holder.GetProperty("CardHolderID"));
        var cards = GetCardsByCardHolder(id);

        return new CardHolderDto(
            Id: id,
            FirstName: ComValue.ToStringOrEmpty(holder.GetProperty("FirstName")),
            LastName: ComValue.ToStringOrEmpty(holder.GetProperty("LastName")),
            Note: ComValue.ToStringOrNull(holder.GetProperty("NoteField")),
            Cards: cards,
            // Držitel sám oprávnění nemá — ukazujeme sjednocení úrovní jeho karet.
            AccessLevelIds: cards.SelectMany(c => c.AccessLevelIds).Distinct().ToList());
    }

    public IReadOnlyList<CardDto> GetCardsByCardHolder(string cardHolderId)
    {
        EnsureSession();
        var args = new object?[] { ComValue.ToLong(cardHolderId), null };
        App.Invoke("GetCardsByCHID", args);

        return ComValue.AsEnumerable(args[1]).Select(com.Wrap).Select(MapCard).ToList();
    }

    public string AddCardHolder(UpsertCardHolderRequest request)
    {
        EnsureSession();
        var holder = com.Create(options.CardHolderProgId);
        ApplyCardHolder(holder, request);

        var args = new object?[] { holder.Target, 0 };
        App.Invoke("AddCardHolder", args);
        WinPakStatus.EnsureCardHolderSucceeded("Založení držitele karty", ComValue.ToInt(args[1]));

        // Příručka: po AddCardHolder nese objekt přidělené id v CardHolderID.
        return ComValue.ToStringOrEmpty(holder.GetProperty("CardHolderID"));
    }

    public void EditCardHolder(string cardHolderId, UpsertCardHolderRequest request)
    {
        EnsureSession();
        var holder = com.Create(options.CardHolderProgId);
        ApplyCardHolder(holder, request);
        holder.SetProperty("CardHolderID", ComValue.ToLong(cardHolderId));

        var args = new object?[] { ComValue.ToLong(cardHolderId), holder.Target, 0 };
        App.Invoke("EditCardHolder", args);
        WinPakStatus.EnsureCardHolderSucceeded("Úprava držitele karty", ComValue.ToInt(args[2]));
    }

    private void ApplyCardHolder(IComDispatch holder, UpsertCardHolderRequest request)
    {
        holder.SetProperty("FirstName", request.FirstName);
        holder.SetProperty("LastName", request.LastName);
        holder.SetProperty("AccountName", options.AccountName);
        if (!string.IsNullOrWhiteSpace(options.SubAccountName))
            holder.SetProperty("SubAccountName", options.SubAccountName);
        if (request.Note is not null)
            holder.SetProperty("NoteField", request.Note);
    }

    // ---------- Karty ----------

    public CardDto? GetCard(string cardNumber)
    {
        EnsureSession();
        var args = new object?[] { cardNumber, options.AccountName, options.SubAccountName, null };
        App.Invoke("GetCardbyCardNumber", args);

        var raw = ComValue.AsEnumerable(args[3]).FirstOrDefault();
        return raw is null ? null : MapCard(com.Wrap(raw));
    }

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
        EnsureSession();
        var existing = GetCard(cardNumber);
        var accessLevelIds = (request.AccessLevelIds ?? existing?.AccessLevelIds ?? [])
            .Select(id => ComValue.ToLong(id))
            .Where(id => id > 0)
            .Distinct()
            .ToArray();

        var args = new object?[]
        {
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
            accessLevelIds,                                                       // alAccessLevelIDs
        };

        App.Invoke("AddUpdateCard", args);
    }

    public void DeleteCard(string cardNumber)
    {
        EnsureSession();
        var args = new object?[] { cardNumber, options.AccountName, options.SubAccountName, 0 };
        App.Invoke("DeleteCard", args);
        WinPakStatus.EnsureCardSucceeded("Zrušení karty", ComValue.ToInt(args[3]));
    }

    /// <summary>Id účtu a podúčtu podle názvů z konfigurace — <c>AddUpdateCard</c> je chce číselně.</summary>
    public (long AccountId, long SubAccountId) ResolveAccountIds()
    {
        var accounts = GetAccounts();
        var account = accounts.FirstOrDefault(a =>
                          a.Name.Equals(options.AccountName, StringComparison.OrdinalIgnoreCase))
                      ?? accounts.FirstOrDefault()
                      ?? throw new InvalidOperationException("WIN-PAK nevrátil žádný účet.");

        var subAccount = account.SubAccounts.FirstOrDefault(s =>
            s.Name.Equals(options.SubAccountName, StringComparison.OrdinalIgnoreCase));

        return (ComValue.ToLong(account.Id), ComValue.ToLong(subAccount?.Id));
    }
}
