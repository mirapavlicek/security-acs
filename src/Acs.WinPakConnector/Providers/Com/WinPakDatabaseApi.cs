using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers.Com;

/// <summary>
/// Obálka nad Database Server API WIN-PAKu (COM objekt <c>NCIHelper.Application</c>).
/// Názvy a pořadí parametrů odpovídají příručce „WIN-PAK 4.9 Database Server API Guide“;
/// mapování je pokryté testy proti atrapě <see cref="IComDispatch"/>.
///
/// Třída je rozdělená do několika souborů podle domén (karty, držitelé, přístupové
/// úrovně, časové zóny, svátky, hardware, systém). Není bezpečná pro souběžné
/// volání — COM+ objekt drží relaci operátora; serializaci zajišťuje
/// <see cref="ComWinPakProvider"/>.
/// </summary>
public sealed partial class WinPakDatabaseApi(IComFactory com, WinPakComOptions options)
{
    // WIN-PAK neumí kartu „bez expirace“ — místo toho se dává datum daleko v budoucnu.
    private static readonly DateTime NoExpiration = new(2099, 12, 31);

    private readonly IComFactory _com = com;
    private readonly WinPakComOptions _options = options;
    private readonly Dictionary<long, string?> _panelNames = [];

    private IComDispatch? _app;
    private int _userId;

    public bool IsLoggedIn => _app is not null && _userId > 0;

    private IComDispatch App => _app ?? throw new InvalidOperationException("Není přihlášeno k WIN-PAK.");

    /// <summary>Přihlásí se a připojí k databázovému serveru. Opakované volání nic nedělá.</summary>
    public void EnsureSession()
    {
        if (IsLoggedIn)
            return;

        var app = _app ??= _com.Create(_options.ApplicationProgId);

        var login = new object?[] { _options.UserName, _options.Password, _options.Domain, 0 };
        app.Invoke("Login", login);
        var userId = ComValue.ToInt(login[3]);
        if (userId <= 0)
        {
            _app = null;
            throw new InvalidOperationException(
                "Přihlášení k WIN-PAK se nezdařilo — ověřte uživatele, heslo a doménu v konfiguraci konektoru.");
        }

        var connect = new object?[] { _options.UserName, _options.Password, _options.Domain, 0, userId };
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
            _app.Invoke("Disconnect", []);
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

    // ---------- Sdílené pomůcky ----------

    /// <summary>Zavolá metodu s <c>[out]</c> parametry a vrátí pole argumentů po volání.</summary>
    private object?[] Call(string method, params object?[] args)
    {
        EnsureSession();
        App.Invoke(method, args);
        return args;
    }

    /// <summary>Zavolá metodu, jejíž poslední parametr je <c>[out]</c> stavový kód zápisu karty.</summary>
    private void CallCardWrite(string operation, string method, params object?[] args)
    {
        var result = Call(method, args);
        WinPakStatus.EnsureCardSucceeded(operation, ComValue.ToInt(result[^1]));
    }

    /// <summary>Zavolá metodu s <c>[out]</c> kolekcí na posledním místě a namapuje prvky.</summary>
    private List<T> CallList<T>(string method, Func<IComDispatch, T> map, params object?[] args)
    {
        var result = Call(method, args);
        return ComValue.AsEnumerable(result[^1]).Select(_com.Wrap).Select(map).ToList();
    }

    private static long[] ToIds(IEnumerable<string>? values)
        => (values ?? []).Select(v => ComValue.ToLong(v)).Where(id => id > 0).Distinct().ToArray();

    // ---------- Účty ----------

    public IReadOnlyList<AccountDto> GetAccounts()
    {
        EnsureSession();
        var args = new object?[] { null };
        App.Invoke("GetAccounts", args);

        var accounts = new List<AccountDto>();
        foreach (var raw in ComValue.AsEnumerable(args[0]))
        {
            var account = _com.Wrap(raw);
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
            .Select(_com.Wrap)
            .Select(sub => new SubAccountDto(
                ComValue.ToStringOrEmpty(sub.GetProperty("AccountID")),
                ComValue.ToStringOrEmpty(sub.GetProperty("AccountName"))))
            .ToList();
    }

    /// <summary>Id účtu a podúčtu podle názvů z konfigurace — zápisové metody je chtějí číselně.</summary>
    public (long AccountId, long SubAccountId) ResolveAccountIds()
    {
        var accounts = GetAccounts();
        var account = accounts.FirstOrDefault(a =>
                          a.Name.Equals(_options.AccountName, StringComparison.OrdinalIgnoreCase))
                      ?? accounts.FirstOrDefault()
                      ?? throw new InvalidOperationException("WIN-PAK nevrátil žádný účet.");

        var subAccount = account.SubAccounts.FirstOrDefault(s =>
            s.Name.Equals(_options.SubAccountName, StringComparison.OrdinalIgnoreCase));

        return (ComValue.ToLong(account.Id), ComValue.ToLong(subAccount?.Id));
    }

    private long AccountId => ResolveAccountIds().AccountId;

    /// <summary>Účet podle id (<c>GetAccountByAcctID</c>).</summary>
    public AccountDto? GetAccount(string accountId)
    {
        var result = Call("GetAccountByAcctID", ComValue.ToLong(accountId), null);
        var raw = ComValue.AsEnumerable(result[1]).FirstOrDefault();
        if (raw is null)
            return null;

        var account = _com.Wrap(raw);
        var id = ComValue.ToStringOrEmpty(account.GetProperty("AccountID"));
        return new AccountDto(id,
            ComValue.ToStringOrEmpty(account.GetProperty("AccountName")),
            GetSubAccounts(ComValue.ToLong(id)));
    }

    public string? GetAccountName(string accountId)
        => ComValue.ToStringOrNull(Call("GetAccountNameByAcctID", ComValue.ToLong(accountId), null)[1]);

    public string? GetSubAccountName(string subAccountId)
        => ComValue.ToStringOrNull(Call("GetSubAccountNameBySubAcctID", ComValue.ToLong(subAccountId), null)[1]);

    // ---------- Čtečky ----------

    public IReadOnlyList<ReaderDto> GetReaders()
    {
        EnsureSession();
        var args = new object?[] { _options.AccountName, null };
        App.Invoke("GetReadersByAccountName", args);

        var readers = new List<ReaderDto>();
        foreach (var raw in ComValue.AsEnumerable(args[1]))
        {
            var device = _com.Wrap(raw);
            var deviceId = ComValue.ToLong(device.GetProperty("DeviceID"));
            readers.Add(new ReaderDto(
                // Comm API adresuje zařízení přes HWDeviceID — používáme ho i jako id čtečky,
                // aby šlo ze stejného identifikátoru rovnou ovládat dveře.
                Id: ComValue.ToStringOrEmpty(device.GetProperty("HWDeviceID")),
                Name: ComValue.ToStringOrEmpty(device.GetProperty("DeviceName")),
                Description: ComValue.ToStringOrNull(device.GetProperty("DeviceDesc")),
                PanelName: GetPanelName(deviceId),
                AccountName: _options.AccountName,
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

    /// <summary>Všechna hardwarová zařízení účtu, nejen čtečky (<c>GetADVDetailsByAccountName</c>).</summary>
    public IReadOnlyList<HardwareDeviceDto> GetHardwareDevices()
        => CallList("GetADVDetailsByAccountName", MapHardwareDevice, _options.AccountName, null);

    private static HardwareDeviceDto MapHardwareDevice(IComDispatch device) => new(
        Hid: ComValue.ToStringOrEmpty(device.GetProperty("HWDeviceID")),
        DeviceId: ComValue.ToStringOrEmpty(device.GetProperty("DeviceID")),
        Name: ComValue.ToStringOrEmpty(device.GetProperty("DeviceName")),
        Description: ComValue.ToStringOrNull(device.GetProperty("DeviceDesc")),
        DeviceType: ComValue.ToStringOrNull(device.GetProperty("DeviceType")));

    /// <summary>Název zařízení podle jeho HID (<c>GetDeviceNameByHWDeviceID</c>).</summary>
    public string? GetDeviceName(long hid)
        => ComValue.ToStringOrNull(Call("GetDeviceNameByHWDeviceID", hid, null)[1]);

    /// <summary>Účet, do kterého zařízení patří (<c>GetAcctIDByHID</c>).</summary>
    public string? GetAccountIdByHid(long hid)
        => ComValue.ToStringOrNull(Call("GetAcctIDByHID", hid, 0)[1]);
}
