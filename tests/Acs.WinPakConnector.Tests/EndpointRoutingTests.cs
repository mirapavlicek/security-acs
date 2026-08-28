using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acs.WinPakConnector.Tests;

/// <summary>
/// Konektor má přes stovku endpointů; dvě stejné cesty se stejnou metodou by se
/// projevily až chybou za běhu při konkrétním požadavku. Proto se kontrolují všechny naráz.
/// </summary>
public sealed class EndpointRoutingTests(ConnectorFactory factory) : IClassFixture<ConnectorFactory>
{
    private static IEnumerable<(string Method, string Pattern)> Routes(EndpointDataSource source)
        => source.Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
                (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => (method, endpoint.RoutePattern.RawText ?? "")));

    [Fact]
    public void Zadne_dva_endpointy_nemaji_stejnou_cestu_i_metodu()
    {
        var source = factory.Services.GetRequiredService<EndpointDataSource>();

        var duplicates = Routes(source)
            .GroupBy(route => route)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key.Method} {group.Key.Pattern}")
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Api_pokryva_vsechny_oblasti_WinPak_API()
    {
        var patterns = Routes(factory.Services.GetRequiredService<EndpointDataSource>())
            .Select(route => route.Pattern)
            .ToHashSet();

        // Kontrolní vzorek napříč doménami, aby se registrace některé skupiny nemohla tiše ztratit.
        string[] expected =
        [
            "/api/v1/status", "/api/v1/accounts", "/api/v1/readers", "/api/v1/access-levels",
            "/api/v1/cards/{cardNumber}", "/api/v1/cards/bulk", "/api/v1/cardholders/search",
            "/api/v1/cardholders/{id}/photo/{index:int}", "/api/v1/time-zones", "/api/v1/time-zones/{id}/ranges",
            "/api/v1/time-zones/{id}/usage", "/api/v1/time-zones/reassign", "/api/v1/holidays",
            "/api/v1/holiday-groups", "/api/v1/panels", "/api/v1/panels/{panelId:long}/outputs",
            "/api/v1/access-areas", "/api/v1/system", "/api/v1/schedules/{id}", "/api/v1/templates/{id}",
            "/api/v1/badges/{id}", "/api/v1/devices/{hid:long}/alarm/acknowledge", "/api/v1/devices/{hid:long}/buffer",
            "/api/v1/panels/{hid:long}/initialize", "/api/v1/doors/lock-all", "/api/v1/doors/schedule",
            "/api/v1/doors/{hid:long}/netaxs-mode", "/api/v1/event-filters", "/api/v1/muster", "/api/v1/events",
        ];

        Assert.Equal([], expected.Where(pattern => !patterns.Contains(pattern)));
    }
}
