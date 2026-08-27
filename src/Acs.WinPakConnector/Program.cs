using System.Runtime.Versioning;
using Acs.WinPakConnector.Auth;
using Acs.WinPakConnector.Models;
using Acs.WinPakConnector.Providers;
using Acs.WinPakConnector.Providers.Com;

var builder = WebApplication.CreateBuilder(args);

// Běh jako Windows služba na WIN-PAK serveru (na Linuxu/dev se chová jako běžný proces).
builder.Services.AddWindowsService(o => o.ServiceName = "AcsWinPakConnector");

builder.Services.AddOpenApi();

builder.Services.Configure<MssqlProviderOptions>(
    builder.Configuration.GetSection(MssqlProviderOptions.SectionName));
builder.Services.Configure<WinPakComOptions>(
    builder.Configuration.GetSection(WinPakComOptions.SectionName));

var providerMode = builder.Configuration["WinPak:Mode"] ?? "Mock";
switch (providerMode.ToLowerInvariant())
{
    case "mock":
        builder.Services.AddSingleton<IWinPakProvider, MockWinPakProvider>();
        break;
    case "mssql":
        builder.Services.AddSingleton<IWinPakProvider, MssqlWinPakProvider>();
        break;
    case "com":
        RegisterComProvider(builder);
        break;
    default:
        throw new InvalidOperationException(
            $"Neznámý WinPak:Mode '{providerMode}'. Povolené hodnoty: Mock, Mssql, Com.");
}

var app = builder.Build();

app.MapOpenApi();
app.UseMiddleware<ApiKeyMiddleware>();

// Jednotné mapování výjimek providerů na HTTP stavy.
app.UseExceptionHandler(exceptionApp => exceptionApp.Run(async context =>
{
    var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    var (status, message) = feature?.Error switch
    {
        NotSupportedException e => (StatusCodes.Status501NotImplemented, e.Message),
        KeyNotFoundException e => (StatusCodes.Status404NotFound, e.Message),
        // Zápis odmítnutý WIN-PAKem je chyba požadavku, ne konektoru.
        WinPakOperationException e => (StatusCodes.Status422UnprocessableEntity, e.Message),
        InvalidOperationException e => (StatusCodes.Status502BadGateway, e.Message),
        _ => (StatusCodes.Status500InternalServerError, "Interní chyba konektoru."),
    };
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new { error = message });
}));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var api = app.MapGroup("/api/v1");

api.MapGet("/info", (IWinPakProvider provider) => new ConnectorInfoDto(
    Version: typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev",
    ProviderMode: provider.Mode,
    SupportsWrite: provider.SupportsWrite,
    SupportsDoorControl: provider.SupportsDoorControl,
    AccountName: provider.AccountName));

api.MapGet("/status", async (IWinPakProvider provider, CancellationToken ct)
    => Results.Ok(await provider.GetStatusAsync(ct)));

api.MapGet("/accounts", async (IWinPakProvider provider, CancellationToken ct)
    => Results.Ok(await provider.GetAccountsAsync(ct)));

api.MapGet("/readers", async (IWinPakProvider provider, CancellationToken ct)
    => Results.Ok(await provider.GetReadersAsync(ct)));

api.MapGet("/access-levels", async (IWinPakProvider provider, CancellationToken ct)
    => Results.Ok(await provider.GetAccessLevelsAsync(ct)));

// ---------- Držitelé karet ----------

api.MapGet("/cardholders", async (string? search, IWinPakProvider provider, CancellationToken ct)
    => Results.Ok(await provider.SearchCardHoldersAsync(search, ct)));

api.MapGet("/cardholders/{id}", async (string id, IWinPakProvider provider, CancellationToken ct)
    => await provider.GetCardHolderAsync(id, ct) is { } cardHolder
        ? Results.Ok(cardHolder)
        : Results.NotFound());

api.MapPost("/cardholders",
    async (UpsertCardHolderRequest request, IWinPakProvider provider, CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(request.LastName))
            return Results.BadRequest(new { error = "LastName je povinné." });

        var id = await provider.AddCardHolderAsync(request, ct);
        return Results.Created($"/api/v1/cardholders/{id}", new { id });
    });

api.MapPut("/cardholders/{id}",
    async (string id, UpsertCardHolderRequest request, IWinPakProvider provider, CancellationToken ct) =>
    {
        await provider.EditCardHolderAsync(id, request, ct);
        return Results.NoContent();
    });

api.MapPost("/cardholders/{id}/access-levels",
    async (string id, AssignAccessLevelRequest request, IWinPakProvider provider, CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(request.AccessLevelId))
            return Results.BadRequest(new { error = "AccessLevelId je povinné." });

        await provider.AssignAccessLevelAsync(id, request.AccessLevelId, ct);
        return Results.NoContent();
    });

api.MapDelete("/cardholders/{id}/access-levels/{accessLevelId}",
    async (string id, string accessLevelId, IWinPakProvider provider, CancellationToken ct) =>
    {
        await provider.RevokeAccessLevelAsync(id, accessLevelId, ct);
        return Results.NoContent();
    });

// ---------- Karty ----------

api.MapGet("/cards/{cardNumber}", async (string cardNumber, IWinPakProvider provider, CancellationToken ct)
    => await provider.GetCardAsync(cardNumber, ct) is { } card
        ? Results.Ok(card)
        : Results.NotFound());

api.MapPut("/cards/{cardNumber}",
    async (string cardNumber, UpsertCardRequest request, IWinPakProvider provider, CancellationToken ct) =>
    {
        await provider.UpsertCardAsync(cardNumber, request, ct);
        return Results.NoContent();
    });

api.MapDelete("/cards/{cardNumber}", async (string cardNumber, IWinPakProvider provider, CancellationToken ct) =>
{
    await provider.DeleteCardAsync(cardNumber, ct);
    return Results.NoContent();
});

// ---------- Hardware (komunikační server) ----------

api.MapGet("/devices", async (IWinPakProvider provider, CancellationToken ct)
    => Results.Ok(await provider.GetDevicesAsync(ct)));

api.MapGet("/doors/{hid:long}", async (long hid, IWinPakProvider provider, CancellationToken ct)
    => Results.Ok(await provider.GetDoorStatusAsync(hid, ct)));

api.MapPost("/doors/{hid:long}/pulse",
    async (long hid, PulseDoorRequest? request, IWinPakProvider provider, CancellationToken ct) =>
    {
        await provider.PulseDoorAsync(hid, request?.Seconds, ct);
        return Results.NoContent();
    });

api.MapPost("/doors/{hid:long}/lock", async (long hid, IWinPakProvider provider, CancellationToken ct) =>
{
    await provider.LockDoorAsync(hid, ct);
    return Results.NoContent();
});

api.MapPost("/doors/{hid:long}/unlock", async (long hid, IWinPakProvider provider, CancellationToken ct) =>
{
    await provider.UnlockDoorAsync(hid, ct);
    return Results.NoContent();
});

api.MapPost("/doors/{hid:long}/mode",
    async (long hid, DoorModeRequest request, IWinPakProvider provider, CancellationToken ct) =>
    {
        if (!Enum.IsDefined(request.Mode))
            return Results.BadRequest(new { error = "Neplatný režim dveří (1–8)." });

        await provider.SetDoorModeAsync(hid, request.Mode, ct);
        return Results.NoContent();
    });

// Události z panelů drží v paměti jen COM provider (odebírá je callback komunikačního serveru).
api.MapGet("/events", async (int? limit, IWinPakProvider provider, CancellationToken ct)
    => provider is ComWinPakProvider com
        ? Results.Ok(await com.GetRecentEventsAsync(limit ?? 100, ct))
        : Results.Json(
            new { error = $"Režim {provider.Mode} události z panelů neodebírá." },
            statusCode: StatusCodes.Status501NotImplemented));

app.Run();

// COM je Windows-only; na jiné platformě konektor nemá jak WIN-PAK oslovit.
[SupportedOSPlatform("windows")]
static void RegisterComProvider(WebApplicationBuilder builder)
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException(
            "Režim Com vyžaduje Windows — WIN-PAK API je vystavené přes COM+/DCOM. " +
            "Na jiné platformě použijte režim Mock.");
    }

    builder.Services.AddSingleton<IComFactory, ComFactory>();
    builder.Services.AddSingleton<IWinPakProvider, ComWinPakProvider>();
}

/// <summary>Zpřístupnění pro integrační testy (WebApplicationFactory).</summary>
public partial class Program;
