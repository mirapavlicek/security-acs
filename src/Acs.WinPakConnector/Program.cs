using Acs.WinPakConnector.Auth;
using Acs.WinPakConnector.Models;
using Acs.WinPakConnector.Providers;

var builder = WebApplication.CreateBuilder(args);

// Běh jako Windows služba na WIN-PAK serveru (na Linuxu/dev se chová jako běžný proces).
builder.Services.AddWindowsService(o => o.ServiceName = "AcsWinPakConnector");

builder.Services.AddOpenApi();

builder.Services.Configure<MssqlProviderOptions>(
    builder.Configuration.GetSection(MssqlProviderOptions.SectionName));

var providerMode = builder.Configuration["WinPak:Mode"] ?? "Mock";
switch (providerMode.ToLowerInvariant())
{
    case "mock":
        builder.Services.AddSingleton<IWinPakProvider, MockWinPakProvider>();
        break;
    case "mssql":
        builder.Services.AddSingleton<IWinPakProvider, MssqlWinPakProvider>();
        break;
    case "sdk":
        builder.Services.AddSingleton<IWinPakProvider, SdkWinPakProvider>();
        break;
    default:
        throw new InvalidOperationException(
            $"Neznámý WinPak:Mode '{providerMode}'. Povolené hodnoty: Mock, Mssql, Sdk.");
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
    SupportsWrite: provider.SupportsWrite));

api.MapGet("/readers", async (IWinPakProvider provider, CancellationToken ct)
    => Results.Ok(await provider.GetReadersAsync(ct)));

api.MapGet("/access-levels", async (IWinPakProvider provider, CancellationToken ct)
    => Results.Ok(await provider.GetAccessLevelsAsync(ct)));

api.MapGet("/cardholders", async (string? search, IWinPakProvider provider, CancellationToken ct)
    => Results.Ok(await provider.SearchCardHoldersAsync(search, ct)));

api.MapGet("/cardholders/{id}", async (string id, IWinPakProvider provider, CancellationToken ct)
    => await provider.GetCardHolderAsync(id, ct) is { } cardHolder
        ? Results.Ok(cardHolder)
        : Results.NotFound());

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

app.Run();

/// <summary>Zpřístupnění pro integrační testy (WebApplicationFactory).</summary>
public partial class Program;
