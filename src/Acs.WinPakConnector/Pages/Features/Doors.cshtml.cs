using Acs.WinPakConnector.Models;
using Acs.WinPakConnector.Providers;
using Microsoft.AspNetCore.Mvc;

namespace Acs.WinPakConnector.Pages.Features;

/// <summary>Ovládání dveří a zařízení přes komunikační server WIN-PAK.</summary>
public class DoorsModel(WinPakProviderCache providers) : FeaturePageModel(providers)
{
    public IReadOnlyList<ReaderDto> Readers { get; private set; } = [];
    public IReadOnlyList<DeviceDto> Devices { get; private set; } = [];

    /// <summary>Zařízení, ke kterému se ukazuje detail (stav dveří, NetAXS režim).</summary>
    [BindProperty(SupportsGet = true)] public long? Hid { get; set; }

    public DoorStatusDto? DoorStatus { get; private set; }
    public NetAxsDoorModeDto? NetAxsMode { get; private set; }
    public int? DoorStatusCode { get; private set; }
    public int? DeviceStatus { get; private set; }
    public int? DefaultReaderMode { get; private set; }
    public string? DetailError { get; private set; }

    /// <summary>Nečíselné id v odkazu — jinak by se detail tiše nezobrazil a nebylo by jasné proč.</summary>
    public string? InvalidHid { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (Hid is null
            && Request.Query.TryGetValue("hid", out var raw)
            && !string.IsNullOrWhiteSpace(raw))
        {
            InvalidHid = raw.ToString();
        }

        await LoadAsync(async () => Readers = await Provider.GetReadersAsync(ct));
        await LoadAsync(async () =>
        {
            try
            {
                Devices = await Provider.GetDevicesAsync(ct);
            }
            catch (NotSupportedException)
            {
                Devices = [];   // režim bez komunikačního serveru — seznam prostě nebude
            }
        });

        if (Hid is { } hid)
            await LoadDetailAsync(hid, ct);
    }

    private async Task LoadDetailAsync(long hid, CancellationToken ct)
    {
        try
        {
            DoorStatus = await Provider.GetDoorStatusAsync(hid, ct);

            if (Catalog is { } catalog)
            {
                DoorStatusCode = await catalog.GetDoorStatusCodeAsync(hid, ct);
                DeviceStatus = await catalog.GetDeviceStatusAsync(hid, 0, ct);
                DefaultReaderMode = await catalog.GetDefaultReaderModeAsync(hid, ct);
                NetAxsMode = await catalog.GetNetAxsDoorModeAsync(hid, ct);
            }
        }
        catch (Exception ex)
        {
            DetailError = ex.Message;
        }
    }

    // ---------- Základní ovládání dveří ----------

    public Task<IActionResult> OnPostUnlockAsync(long hid, CancellationToken ct)
        => ActAsync($"Odemknutí dveří {hid}", () => Provider.UnlockDoorAsync(hid, ct));

    public Task<IActionResult> OnPostLockAsync(long hid, CancellationToken ct)
        => ActAsync($"Zamknutí dveří {hid}", () => Provider.LockDoorAsync(hid, ct));

    public Task<IActionResult> OnPostPulseAsync(long hid, int? seconds, CancellationToken ct)
        => ActAsync(seconds is > 0 ? $"Otevření dveří {hid} na {seconds} s" : $"Otevření dveří {hid}",
            () => Provider.PulseDoorAsync(hid, seconds, ct));

    public Task<IActionResult> OnPostModeAsync(long hid, DoorMode mode, CancellationToken ct)
        => ActAsync($"Nastavení režimu dveří {hid} na {mode}", () => Provider.SetDoorModeAsync(hid, mode, ct));

    public Task<IActionResult> OnPostEntryPointAsync(long hid, int point, bool unlock, CancellationToken ct)
        => ActAsync($"{(unlock ? "Odemknutí" : "Zamknutí")} vstupního bodu {hid}/{point}",
            () => RequireCatalog().LockEntryPointAsync(hid, point, unlock, ct));

    public Task<IActionResult> OnPostNetAxsModeAsync(long hid, NetAxsDoorModeDto mode, CancellationToken ct)
        => ActAsync($"Nastavení NetAXS režimu dveří {hid}",
            () => RequireCatalog().SetNetAxsDoorModeAsync(hid, mode, ct));

    // ---------- Alarmy ----------

    public Task<IActionResult> OnPostAcknowledgeAsync(long hid, int point, CancellationToken ct)
        => ActAsync($"Potvrzení alarmu {hid}/{point}", () => RequireCatalog().AcknowledgeAlarmAsync(hid, point, ct));

    public Task<IActionResult> OnPostClearAlarmAsync(long hid, int point, CancellationToken ct)
        => ActAsync($"Zrušení alarmu {hid}/{point}", () => RequireCatalog().ClearAlarmAsync(hid, point, ct));

    public Task<IActionResult> OnPostNoteAsync(long hid, int point, string note, CancellationToken ct)
        => ActAsync($"Poznámka k transakci {hid}/{point}", () => RequireCatalog().AddNoteAsync(hid, point, note, ct));

    public Task<IActionResult> OnPostTransactionAsync(long hid, int point, CancellationToken ct)
        => ActAsync($"Detail transakce {hid}/{point}", async () =>
        {
            var detail = await RequireCatalog().GetTransactionDetailsAsync(hid, point, ct);
            return detail.Details ?? "WIN-PAK nevrátil žádný detail.";
        });

    public Task<IActionResult> OnPostShuntAsync(long hid, bool shunt, CancellationToken ct)
        => ActAsync($"{(shunt ? "Shuntování" : "Zrušení shuntu")} alarmu {hid}",
            () => RequireCatalog().ShuntAlarmAsync(hid, shunt, ct));

    public Task<IActionResult> OnPostUnshuntPointAsync(long hid, int point, CancellationToken ct)
        => ActAsync($"Zrušení shuntu bodu {hid}/{point}", () => RequireCatalog().UnshuntAlarmPointAsync(hid, point, ct));

    // ---------- Ostatní povely ----------

    public Task<IActionResult> OnPostBufferAsync(long hid, int mode, bool buffer, CancellationToken ct)
        => ActAsync($"{(buffer ? "Zapnutí" : "Vypnutí")} bufferu zařízení {hid}",
            () => RequireCatalog().BufferAsync(hid, mode, buffer, ct));

    public Task<IActionResult> OnPostEnergizeAsync(long hid, bool energize, CancellationToken ct)
        => ActAsync($"{(energize ? "Sepnutí" : "Rozepnutí")} výstupu {hid}",
            () => RequireCatalog().EnergizeAsync(hid, energize, ct));

    public Task<IActionResult> OnPostRestoreTimeZoneAsync(long hid, CancellationToken ct)
        => ActAsync($"Návrat zařízení {hid} pod časovou zónu", () => RequireCatalog().RestoreTimeZoneAsync(hid, ct));

    public Task<IActionResult> OnPostCustomCommandAsync(long hid, string command, CancellationToken ct)
        => ActAsync($"Vlastní příkaz pro zařízení {hid}", () => RequireCatalog().ExecuteCustomCommandAsync(hid, command, ct));
}
