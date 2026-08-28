using System.Runtime.InteropServices;
using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers.Com;

/// <summary>
/// Callback, který komunikační server volá při každé události nebo alarmu.
/// Příručka jej popisuje jako rozhraní <c>IWPAVCallBack</c> s metodami
/// <c>GotMessage</c> a <c>ServerError</c>; předává se do <c>InitServer</c>
/// jako <c>IDispatch* Caller</c>.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public sealed class WinPakCallbackSink
{
    public event Action<string>? MessageReceived;
    public event Action<string>? ErrorReceived;

    public void GotMessage(string sMessage) => MessageReceived?.Invoke(sMessage);

    public void ServerError(string sError) => ErrorReceived?.Invoke(sError);
}

/// <summary>
/// Obálka nad Communication Server API (COM objekt <c>ACCW.MTSCBServer</c>):
/// stav a ovládání dveří plus odběr událostí z panelů.
/// </summary>
public sealed partial class WinPakCommApi(IComFactory com, WinPakComOptions options)
{
    private readonly IComFactory _com = com;
    private readonly WinPakComOptions _options = options;

    /// <summary>Typ serveru pro <c>IsConnected2</c> — 0 znamená všechny.</summary>
    private const int AllServers = 0;

    private readonly WinPakCallbackSink _sink = new();
    private readonly Queue<WinPakEvent> _events = new();
    private readonly Lock _eventsLock = new();

    private IComDispatch? _server;

    public bool IsStarted => _server is not null;

    private IComDispatch Server => _server ?? throw new InvalidOperationException(
        "Komunikační server WIN-PAK není připojen (WinPak:Com:EnableCommunicationServer).");

    public void EnsureStarted()
    {
        if (_server is not null)
            return;

        var server = _com.Create(_options.CommServerProgId);
        _sink.MessageReceived += OnMessage;

        // InitServer2 se používá při přihlašování doménovými údaji, jinak stačí InitServer.
        var connected = string.IsNullOrWhiteSpace(_options.Domain)
            ? ComValue.ToBool(server.Invoke("InitServer",
                [_sink, _options.CommViewType, _options.UserName, _options.Password, 0]))
            : ComValue.ToBool(server.Invoke("InitServer2",
                [_sink, _options.CommViewType, _options.UserName, _options.Password, _options.Domain, 0]));

        if (!connected)
        {
            _sink.MessageReceived -= OnMessage;
            throw new InvalidOperationException(
                "Registrace u komunikačního serveru WIN-PAK selhala (InitServer vrátil false).");
        }

        _server = server;
    }

    public void Close()
    {
        if (_server is null)
            return;

        try
        {
            _server.Invoke("DoneServer", [_sink]);
        }
        catch (Exception)
        {
            // Odhlášení je best-effort.
        }

        _sink.MessageReceived -= OnMessage;
        _server = null;
    }

    private void OnMessage(string message)
    {
        var parsed = WinPakEvent.Parse(message);
        if (parsed.Count == 0)
            return;

        lock (_eventsLock)
        {
            foreach (var winPakEvent in parsed)
            {
                _events.Enqueue(winPakEvent);
                while (_events.Count > Math.Max(_options.EventBufferSize, 1))
                    _events.Dequeue();
            }
        }
    }

    /// <summary>Poslední události z panelů (kruhový buffer v paměti konektoru).</summary>
    public IReadOnlyList<WinPakEvent> GetRecentEvents(int limit)
    {
        lock (_eventsLock)
            return _events.TakeLast(Math.Clamp(limit, 1, _options.EventBufferSize)).ToList();
    }

    public IReadOnlyList<ServerStatusDto> GetServerStatus()
    {
        EnsureStarted();
        var status = ComValue.ToStringOrNull(Server.Invoke("IsConnected2", [AllServers]));
        return NlzMessage.ParseServerStatus(status);
    }

    public IReadOnlyList<DeviceDto> ListConnectedDevices()
    {
        EnsureStarted();
        var args = new object?[] { null };
        var returned = Server.Invoke("ListConnectedDevices", args);

        return ComValue.AsEnumerable(returned ?? args[0])
            .Select(_com.Wrap)
            .Select(device => new DeviceDto(
                Hid: ComValue.ToStringOrEmpty(device.GetProperty("HWDeviceID")),
                Name: ComValue.ToStringOrEmpty(device.GetProperty("DeviceName")),
                DeviceType: ComValue.ToStringOrNull(device.GetProperty("DeviceType"))))
            .ToList();
    }

    public DoorStatusDto GetDoorStatus(long hid)
    {
        EnsureStarted();
        var raw = Server.Invoke("GetDoorStatus2", [hid]);
        return NlzMessage.ParseDoorStatus(hid, ComValue.ToStringOrNull(raw));
    }

    public void LockDoor(long hid)
    {
        EnsureStarted();
        Server.Invoke("EntryPointLockByID", [hid]);
    }

    public void UnlockDoor(long hid)
    {
        EnsureStarted();
        Server.Invoke("EntryPointUnLockByID", [hid]);
    }

    /// <summary>Krátké otevření; se zadanou délkou se použije časovaný puls (jednotka 0 = sekundy).</summary>
    public void Pulse(long hid, int? seconds)
    {
        EnsureStarted();
        if (seconds is > 0)
            Server.Invoke("TimedPulseByHID", [hid, 0, seconds.Value]);
        else
            Server.Invoke("PulseByHID", [hid]);
    }

    public void SetDoorMode(long hid, DoorMode mode)
    {
        EnsureStarted();
        Server.Invoke("DoorModeByHID", [hid, (int)mode]);
    }
}
