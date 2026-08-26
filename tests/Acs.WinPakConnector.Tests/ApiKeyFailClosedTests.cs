using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Acs.WinPakConnector.Tests;

/// <summary>Bez nakonfigurovaného API klíče musí být API nedostupné (fail-closed).</summary>
public sealed class ApiKeyFailClosedTests : IClassFixture<NoApiKeyFactory>
{
    private readonly NoApiKeyFactory _factory;

    public ApiKeyFailClosedTests(NoApiKeyFactory factory) => _factory = factory;

    [Fact]
    public async Task Api_WithoutConfiguredKey_Returns503()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/readers");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Health_StillWorks_WithoutConfiguredKey()
    {
        var response = await _factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

public sealed class NoApiKeyFactory : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Security:ApiKey"] = "",
                ["WinPak:Mode"] = "Mock",
            }));
        return base.CreateHost(builder);
    }
}
