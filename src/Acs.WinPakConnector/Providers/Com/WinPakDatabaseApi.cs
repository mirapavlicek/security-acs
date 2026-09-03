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
    private string? _resolvedAccountName;
    private string? _resolvedSubAccountName;

    public bool IsLoggedIn => _app is not null && _userId > 0;

    /// <summary>
    /// Účet WIN-PAK, se kterým se pracuje. Karty, držitelé i čtečky jsou po účtech
    /// oddělení, takže dotaz s prázdným účtem vrátí prázdno — a přesně to se stalo
    /// při prvním zprovoznění („0 čteček, 0 držitelů“ u účtu, který nikdo nevyplnil).
    /// Když v konfiguraci účet není a WIN-PAK má jediný, použije se ten; při více
    /// účtech se musí vybrat v konfiguraci.
    /// </summary>
    public string AccountName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_options.AccountName))
                return _options.AccountName;

            if (_resolvedAccountName is not null)
                return _resolvedAccountName;

            var accounts = GetAccounts();
            return _resolvedAccountName = accounts.Count switch
            {
                0 => throw new InvalidOperationException("WIN-PAK nevrátil žádný účet."),
                1 => accounts[0].Name,
                _ => throw new InvalidOperationException(
                    "WIN-PAK má více účtů a v konfiguraci konektoru není vybraný žádný: "
                    + string.Join(", ", accounts.Select(a => a.Name))),
            };
        }
    }

    /// <summary>Byl účet doplněn automaticky (jediný ve WIN-PAKu), ne z konfigurace?</summary>
    public bool AccountNameResolvedAutomatically
        => string.IsNullOrWhiteSpace(_options.AccountName) && _resolvedAccountName is not null;

    /// <summary>
    /// Podúčet, se kterým se pracuje. Držitelé a přístupové úrovně jsou ve WIN-PAKu
    /// pod podúčtem; s prázdným podúčtem vrátil server nuly i u správného účtu
    /// (čtečky a časové zóny, které podúčet neberou, přitom fungovaly). Když
    /// podúčet v konfiguraci není a účet má jediný, použije se ten; jinak prázdný.
    /// </summary>
    public string SubAccountName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_options.SubAccountName))
                return _options.SubAccountName;

            if (_resolvedSubAccountName is not null)
                return _resolvedSubAccountName;

            var account = GetAccounts().FirstOrDefault(a =>
                a.Name.Equals(AccountName, StringComparison.OrdinalIgnoreCase));
            return _resolvedSubAccountName = account?.SubAccounts.Count == 1 ? account.SubAccounts[0].Name : "";
        }
    }

    /// <summary>Byl podúčet doplněn automaticky (jediný u účtu)?</summary>
    public bool SubAccountNameResolvedAutomatically
        => string.IsNullOrWhiteSpace(_options.SubAccountName) && !string.IsNullOrEmpty(_resolvedSubAccountName);

    /// <summary>
    /// Účet, když je jednoznačný; jinak null. Pro dotazy, které mají variantu
    /// „za všechny účty“ (časové zóny, přístupové úrovně, svátky).
    /// </summary>
    public string? AccountNameOrNull
    {
        get
        {
            try
            {
                return AccountName;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    private IComDispatch App => _app is null
        ? throw new InvalidOperationException("Není přihlášeno k WIN-PAK.")
        : new ReconnectingDispatch(this);

    /// <summary>
    /// Volání na aplikační objekt s obnovou relace. Když COM+ server WIN-PAKu spadne
    /// (na ostrém: „The remote procedure call failed“ a pak „The RPC server is
    /// unavailable“ u všeho dalšího), proxy je mrtvá a dřív zůstala mrtvá až do
    /// restartu služby. Teď se relace zahodí, přihlásí znovu a volání jednou
    /// zopakuje — odmítnuté RPC se na serveru neprovedlo.
    /// </summary>
    private sealed class ReconnectingDispatch(WinPakDatabaseApi api) : IComDispatch
    {
        public object Target => api._app!.Target;

        public object? Invoke(string method, object?[] args)
            => Retry(() => api._app!.Invoke(method, args));

        public object? GetProperty(string name)
            => Retry(() => api._app!.GetProperty(name));

        public object? GetProperty(string name, object?[] index)
            => Retry(() => api._app!.GetProperty(name, index));

        public void SetProperty(string name, object? value)
            => Retry(() => { api._app!.SetProperty(name, value); return (object?)null; });

        public void SetProperty(string name, object?[] index, object? value)
            => Retry(() => { api._app!.SetProperty(name, index, value); return (object?)null; });

        private object? Retry(Func<object?> call)
        {
            try
            {
                return call();
            }
            catch (ComCallException ex) when (ex.IsConnectionLost)
            {
                api.ResetSession();
                api.EnsureSession();
                return call();
            }
        }
    }

    /// <summary>
    /// Opustí relaci, na které visí nedokončené volání: objekt se nechá uvázlému vláknu
    /// a další volání si vytvoří nový. Neodhlašuje se — na zablokovaném serveru by
    /// to viselo stejně.
    /// </summary>
    public void AbandonSession() => ResetSession();

    /// <summary>Zahodí mrtvou relaci bez pokusu o odhlášení — server, který by ho přijal, už neběží.</summary>
    private void ResetSession()
    {
        _app = null;
        _userId = 0;
        _resolvedAccountName = null;
        _resolvedSubAccountName = null;
        _panelNames.Clear();
    }

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
        _resolvedAccountName = null;
        _resolvedSubAccountName = null;
        _panelNames.Clear();
    }

    /// <summary>
    /// Příručka uvádí <c>IsConnected(out connected)</c> bez typu. Zkusí se číselný
    /// [out] parametr a pak logický — COM při nesedícím typu odmítne volání, ne hodnotu.
    /// </summary>
    public bool IsConnected()
    {
        if (_app is null)
            return false;

        try
        {
            var numeric = new object?[] { 0 };
            App.Invoke("IsConnected", numeric);
            return ComValue.ToBool(numeric[0]);
        }
        catch (ComCallException)
        {
            var boolean = new object?[] { false };
            App.Invoke("IsConnected", boolean);
            return ComValue.ToBool(boolean[0]);
        }
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

    /// <summary>
    /// Seznam, jehož prvky mohou být buď objekty s vlastnostmi, nebo prosté hodnoty
    /// (řetězce, čísla) — WIN-PAK obojí střídá podle volání. Prostá hodnota se
    /// namapuje přes <paramref name="fromValue"/> s pořadím od 1.
    /// </summary>
    private List<T> CallNamedList<T>(string method, Func<string, int, T> fromValue,
        Func<IComDispatch, T> fromObject, params object?[] args)
    {
        var result = Call(method, args);
        return ComValue.AsEnumerable(result[^1])
            .Select((raw, position) => raw is string or int or long or double or decimal
                ? fromValue(ComValue.ToStringOrEmpty(raw), position + 1)
                : fromObject(_com.Wrap(raw!)))
            .ToList();
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
                          a.Name.Equals(AccountName, StringComparison.OrdinalIgnoreCase))
                      ?? accounts.FirstOrDefault()
                      ?? throw new InvalidOperationException("WIN-PAK nevrátil žádný účet.");

        var subAccount = account.SubAccounts.FirstOrDefault(s =>
            s.Name.Equals(SubAccountName, StringComparison.OrdinalIgnoreCase));

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
        var args = new object?[] { AccountName, null };
        App.Invoke("GetReadersByAccountName", args);

        var rawReaders = ComValue.AsEnumerable(args[1]).Select(_com.Wrap).ToList();

        // Název zařízení podle DeviceID se dřív dotahoval pro každou čtečku zvlášť —
        // u 785 čteček 785 COM roundtripů (a s opakováním po Type mismatch dvakrát
        // tolik). Jeden výpis všech zařízení účtu dá totéž jedním voláním; na dotaz
        // po jednom se sáhne jen u id, které ve výpisu chybí.
        var deviceIds = rawReaders.Select(d => ComValue.ToLong(d.GetProperty("DeviceID"))).Where(id => id > 0).Distinct().ToList();
        if (deviceIds.Any(id => !_panelNames.ContainsKey(id)))
        {
            foreach (var device in GetHardwareDevices())
            {
                var id = ComValue.ToLong(device.DeviceId);
                if (id > 0 && !_panelNames.ContainsKey(id))
                    _panelNames[id] = device.Name;
            }
        }

        var readers = new List<ReaderDto>(rawReaders.Count);
        foreach (var device in rawReaders)
        {
            var deviceId = ComValue.ToLong(device.GetProperty("DeviceID"));
            readers.Add(new ReaderDto(
                // Comm API adresuje zařízení přes HWDeviceID — používáme ho i jako id čtečky,
                // aby šlo ze stejného identifikátoru rovnou ovládat dveře.
                Id: ComValue.ToStringOrEmpty(device.GetProperty("HWDeviceID")),
                Name: ComValue.ToStringOrEmpty(device.GetProperty("DeviceName")),
                Description: ComValue.ToStringOrNull(device.GetProperty("DeviceDesc")),
                PanelName: GetPanelName(deviceId),
                AccountName: AccountName,
                IsActive: true));
        }

        return readers;
    }

    /// <summary>Název zařízení podle DeviceID; po hromadném naplnění z výpisu zařízení už jen z paměti.</summary>
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
        => CallList("GetADVDetailsByAccountName", MapHardwareDevice, AccountName, null);

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
