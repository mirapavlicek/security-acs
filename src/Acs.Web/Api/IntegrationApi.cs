using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Acs.Infrastructure.Integration;
using Acs.Infrastructure.Settings;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Acs.Web.Api;

/// <summary>
/// Integrační API ACS pro parkovací systém (GreenCenter) — podmnožina kontraktu
/// <c>docs/integrace/acs-integration-api.yaml</c>: online autorizace u vjezdu (vzor C),
/// hlášení událostí a čtení osob / identifikátorů / oprávnění pro vlastní synchronizaci
/// systému (vzor B). Přihlášení API klíčem v hlavičce <c>X-Api-Key</c>; bez zapnutí
/// a nastaveného klíče API odmítá všechno (fail-closed).
/// </summary>
public static class IntegrationApi
{
    public const string Prefix = "/api/integration/v1";

    public static IEndpointRouteBuilder MapIntegrationApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Prefix)
            .AllowAnonymous()
            .DisableAntiforgery()
            .AddEndpointFilter<ParkingApiKeyFilter>();

        group.MapGet("/", () => Results.Ok(new
        {
            contractVersion = IntegrationContract.Version,
            integration = IntegrationContract.ParkingSource,
            operations = new[]
            {
                "checkAuthorization", "reportEvents", "listPersons", "getPerson", "listPersonCredentials",
                "listEntitlements", "listAccessPoints",
            },
        }));

        group.MapPost("/authorization-checks", async (AuthorizationRequest? request, GateAuthorizationService gate,
            CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.AccessPointId))
                return Problem(400, "Chybí accessPointId.");
            if (request.Credential is null || string.IsNullOrWhiteSpace(request.Credential.Value))
                return Problem(400, "Chybí credential.value.");
            if (IntegrationContract.ParseCredentialType(request.Credential.Type) is null)
                return Problem(400, $"Neznámý druh identifikátoru „{request.Credential.Type}“ (card, licensePlate, pin, tag, biometric, other).");
            if (request.Direction is { Length: > 0 } && request.Direction is not ("in" or "out"))
                return Problem(400, "direction musí být in nebo out.");

            var decision = await gate.DecideAsync(request, ct);
            return Results.Ok(decision);
        });

        group.MapPost("/events", async (EventBatch? batch, IntegrationEventService events, CancellationToken ct) =>
        {
            if (batch?.Events is not { Count: > 0 })
                return Problem(400, "Dávka neobsahuje žádnou událost.");
            if (batch.Events.Count > IntegrationEventService.MaxBatchSize)
                return Problem(413, $"Dávka má víc než {IntegrationEventService.MaxBatchSize} událostí.");

            var result = await events.ReportAsync(IntegrationContract.ParkingSource, batch.Events, ct);
            return Results.Json(result, statusCode: StatusCodes.Status202Accepted);
        });

        group.MapGet("/persons", async (DateTime? changedSince, string? cursor, int? limit,
                IntegrationCatalogService catalog, CancellationToken ct)
            => Results.Ok(await catalog.ListPersonsAsync(changedSince, cursor, limit, ct)));

        group.MapGet("/persons/{personId}", async (string personId, IntegrationCatalogService catalog, CancellationToken ct) =>
        {
            if (!int.TryParse(personId, out var id))
                return Problem(404, "Osoba nenalezena.");
            var person = await catalog.GetPersonAsync(id, ct);
            return person is null ? Problem(404, "Osoba nenalezena.") : Results.Ok(person);
        });

        group.MapGet("/persons/{personId}/credentials", async (string personId, IntegrationCatalogService catalog,
            CancellationToken ct) =>
        {
            if (!int.TryParse(personId, out var id))
                return Problem(404, "Osoba nenalezena.");
            var credentials = await catalog.GetCredentialsAsync(id, ct);
            return credentials is null ? Problem(404, "Osoba nenalezena.") : Results.Ok(new { items = credentials });
        });

        group.MapGet("/entitlements", async (DateTime? changedSince, string? personId, string? status, string? cursor,
                int? limit, IntegrationCatalogService catalog, CancellationToken ct)
            => Results.Ok(await catalog.ListEntitlementsAsync(changedSince, personId, status, cursor, limit, ct)));

        group.MapGet("/access-points", async (IntegrationCatalogService catalog, CancellationToken ct)
            => Results.Ok(new { items = await catalog.ListAccessPointsAsync(ct) }));

        return app;
    }

    public sealed record EventBatch(List<IntegrationEventInput> Events);

    internal static IResult Problem(int status, string detail)
        => Results.Problem(detail: detail, statusCode: status, title: ReasonPhrases(status));

    private static string ReasonPhrases(int status) => status switch
    {
        400 => "Požadavek je vadný.",
        401 => "Chybí nebo neplatí API klíč.",
        403 => "Přístup z této adresy není povolen.",
        404 => "Nenalezeno.",
        413 => "Dávka je nad povolený limit.",
        503 => "Integrace není zapnutá.",
        _ => "Chyba.",
    };
}

/// <summary>
/// Ověření API klíče parkovacího systému. Klíč se porovnává v konstantním čase; volitelně
/// se omezí i zdrojová IP adresa (za HAProxy z <c>X-Forwarded-For</c>, viz ForwardedHeaders).
/// </summary>
public sealed class ParkingApiKeyFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var settings = http.RequestServices.GetRequiredService<SettingsService>();
        var ct = http.RequestAborted;

        if (!await settings.GetBoolAsync(SettingKeys.ParkingSystemEnabled, false, ct))
            return IntegrationApi.Problem(503, "Integrace parkovacího systému není zapnutá (Nastavení → Parkovací systém).");

        var expected = await settings.GetAsync(SettingKeys.ParkingSystemApiKey, ct);
        if (string.IsNullOrEmpty(expected))
            return IntegrationApi.Problem(503, "Integrace nemá nastavený API klíč — API zůstává zavřené.");

        var provided = http.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(provided) || !FixedTimeEquals(expected, provided))
        {
            http.Response.Headers.WWWAuthenticate = "ApiKey realm=\"acs-integration\"";
            return IntegrationApi.Problem(401, "Chybí nebo neplatí API klíč (hlavička X-Api-Key).");
        }

        var allowedIps = await settings.GetAsync(SettingKeys.ParkingSystemAllowedIps, ct);
        if (!IsAllowedIp(allowedIps, http.Connection.RemoteIpAddress))
            return IntegrationApi.Problem(403, "Volání z této IP adresy není povoleno.");

        return await next(context);
    }

    private static bool FixedTimeEquals(string expected, string provided)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(provided);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    internal static bool IsAllowedIp(string? allowedList, IPAddress? remote)
    {
        if (string.IsNullOrWhiteSpace(allowedList))
            return true;
        if (remote is null)
            return false;
        if (remote.IsIPv4MappedToIPv6)
            remote = remote.MapToIPv4();

        foreach (var entry in allowedList.Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(entry, out var ip) && ip.Equals(remote))
                return true;
            if (System.Net.IPNetwork.TryParse(entry, out var network) && network.Contains(remote))
                return true;
        }
        return false;
    }
}

/// <summary>Časy v API jsou vždy UTC — hodnoty z DB jsou bez druhu, převod na „Z“ dělá serializace.</summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetDateTime();
        return value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime());
}
