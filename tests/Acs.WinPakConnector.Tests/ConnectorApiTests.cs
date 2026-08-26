using System.Net;
using System.Net.Http.Json;
using Acs.WinPakConnector.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Acs.WinPakConnector.Tests;

public sealed class ConnectorApiTests : IClassFixture<ConnectorFactory>
{
    private const string ApiKey = "test-api-key";
    private readonly ConnectorFactory _factory;

    public ConnectorApiTests(ConnectorFactory factory) => _factory = factory;

    private HttpClient CreateClient(bool withApiKey = true)
    {
        var client = _factory.CreateClient();
        if (withApiKey)
            client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }

    [Fact]
    public async Task Health_IsAccessible_WithoutApiKey()
    {
        var response = await CreateClient(withApiKey: false).GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Api_WithoutApiKey_Returns401()
    {
        var response = await CreateClient(withApiKey: false).GetAsync("/api/v1/readers");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Api_WithWrongApiKey_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "spatny-klic");
        var response = await client.GetAsync("/api/v1/readers");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Readers_ReturnsMockData()
    {
        var readers = await CreateClient().GetFromJsonAsync<List<ReaderDto>>("/api/v1/readers");
        Assert.NotNull(readers);
        Assert.NotEmpty(readers);
        Assert.Contains(readers, r => r.Name == "Hlavní vchod");
    }

    [Fact]
    public async Task AccessLevels_ReturnsMockData()
    {
        var levels = await CreateClient().GetFromJsonAsync<List<AccessLevelDto>>("/api/v1/access-levels");
        Assert.NotNull(levels);
        Assert.NotEmpty(levels);
    }

    [Fact]
    public async Task CardHolders_Search_FiltersByName()
    {
        var holders = await CreateClient()
            .GetFromJsonAsync<List<CardHolderDto>>("/api/v1/cardholders?search=novák");
        Assert.NotNull(holders);
        var holder = Assert.Single(holders);
        Assert.Equal("Novák", holder.LastName);
    }

    [Fact]
    public async Task CardHolder_UnknownId_Returns404()
    {
        var response = await CreateClient().GetAsync("/api/v1/cardholders/CH-9999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AssignAndRevokeAccessLevel_UpdatesCardHolder()
    {
        var client = CreateClient();

        var assign = await client.PostAsJsonAsync(
            "/api/v1/cardholders/CH-1003/access-levels",
            new AssignAccessLevelRequest("AL-03"));
        Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);

        var holder = await client.GetFromJsonAsync<CardHolderDto>("/api/v1/cardholders/CH-1003");
        Assert.NotNull(holder);
        Assert.Contains("AL-03", holder.AccessLevelIds);

        var revoke = await client.DeleteAsync("/api/v1/cardholders/CH-1003/access-levels/AL-03");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        holder = await client.GetFromJsonAsync<CardHolderDto>("/api/v1/cardholders/CH-1003");
        Assert.NotNull(holder);
        Assert.DoesNotContain("AL-03", holder.AccessLevelIds);
    }

    [Fact]
    public async Task AssignAccessLevel_UnknownCardHolder_Returns404()
    {
        var response = await CreateClient().PostAsJsonAsync(
            "/api/v1/cardholders/CH-9999/access-levels",
            new AssignAccessLevelRequest("AL-01"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Info_ReportsMockProvider()
    {
        var info = await CreateClient().GetFromJsonAsync<ConnectorInfoDto>("/api/v1/info");
        Assert.NotNull(info);
        Assert.Equal("Mock", info.ProviderMode);
        Assert.True(info.SupportsWrite);
    }
}

public sealed class ConnectorFactory : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Security:ApiKey"] = "test-api-key",
                ["WinPak:Mode"] = "Mock",
            }));
        return base.CreateHost(builder);
    }
}
