using System.Security.Cryptography;
using System.Text;

namespace Acs.WinPakConnector.Auth;

/// <summary>
/// Jednoduchá ochrana API klíčem: každý požadavek na /api/* musí nést hlavičku
/// <c>X-Api-Key</c> se správnou hodnotou. Bez nakonfigurovaného klíče
/// (<c>Security:ApiKey</c>) jsou všechny /api/* požadavky odmítnuty (fail-closed).
/// Endpoint /health je vždy povolen (healthcheck HAProxy / monitoringu).
/// </summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, IConfiguration configuration, ILogger<ApiKeyMiddleware> logger)
{
    public const string HeaderName = "X-Api-Key";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        var configuredKey = configuration["Security:ApiKey"];
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            logger.LogWarning("Security:ApiKey není nakonfigurován — všechny /api požadavky jsou odmítány.");
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { error = "Konektor nemá nakonfigurovaný API klíč." });
            return;
        }

        var providedKey = context.Request.Headers[HeaderName].FirstOrDefault();
        if (providedKey is null || !FixedTimeEquals(providedKey, configuredKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Neplatný nebo chybějící X-Api-Key." });
            return;
        }

        await next(context);
    }

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
