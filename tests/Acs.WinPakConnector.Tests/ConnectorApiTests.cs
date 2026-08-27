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
        Assert.True(info.SupportsDoorControl);
    }

    [Fact]
    public async Task Status_ReportsConnectedServers()
    {
        var status = await CreateClient().GetFromJsonAsync<ConnectorStatusDto>("/api/v1/status");

        Assert.NotNull(status);
        Assert.True(status.DatabaseServerConnected);
        Assert.NotEmpty(status.Servers);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task Accounts_ReturnAccountWithSubAccounts()
    {
        var accounts = await CreateClient().GetFromJsonAsync<List<AccountDto>>("/api/v1/accounts");

        var account = Assert.Single(accounts!);
        Assert.NotEmpty(account.SubAccounts);
    }

    [Fact]
    public async Task Card_IsReadableByNumber_AndCarriesAccessLevels()
    {
        var card = await CreateClient().GetFromJsonAsync<CardDto>("/api/v1/cards/100234");

        Assert.NotNull(card);
        Assert.Equal("CH-1001", card.CardHolderId);
        Assert.Equal(CardStatus.Active, card.Status);
        Assert.Contains("AL-01", card.AccessLevelIds);
    }

    [Fact]
    public async Task UnknownCard_Returns404()
    {
        var response = await CreateClient().GetAsync("/api/v1/cards/000000");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Card_CanBeUpsertedAndDeleted()
    {
        var client = CreateClient();

        var upsert = await client.PutAsJsonAsync("/api/v1/cards/200999",
            new UpsertCardRequest("CH-1001", CardStatus.Active, 1, new DateTime(2026, 1, 1), null, null, ["AL-03"]));
        Assert.Equal(HttpStatusCode.NoContent, upsert.StatusCode);

        var card = await client.GetFromJsonAsync<CardDto>("/api/v1/cards/200999");
        Assert.NotNull(card);
        Assert.Equal(["AL-03"], card.AccessLevelIds);

        var delete = await client.DeleteAsync("/api/v1/cards/200999");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/cards/200999")).StatusCode);
    }

    [Fact]
    public async Task CardHolder_CanBeCreatedAndEdited()
    {
        var client = CreateClient();

        var created = await client.PostAsJsonAsync("/api/v1/cardholders",
            new UpsertCardHolderRequest("Eva", "Malá", "Recepce"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var id = (await created.Content.ReadFromJsonAsync<CreatedCardHolder>())!.Id;
        var edit = await client.PutAsJsonAsync($"/api/v1/cardholders/{id}",
            new UpsertCardHolderRequest("Eva", "Nováková", "Recepce"));
        Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);

        var holder = await client.GetFromJsonAsync<CardHolderDto>($"/api/v1/cardholders/{id}");
        Assert.Equal("Nováková", holder!.LastName);
    }

    [Fact]
    public async Task CardHolder_WithoutLastName_Returns400()
    {
        var response = await CreateClient().PostAsJsonAsync("/api/v1/cardholders",
            new UpsertCardHolderRequest("Eva", "", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AccessLevel_IsWrittenOntoTheHoldersCards()
    {
        var client = CreateClient();

        await client.PostAsJsonAsync("/api/v1/cardholders/CH-1002/access-levels",
            new AssignAccessLevelRequest("AL-03"));

        var holder = await client.GetFromJsonAsync<CardHolderDto>("/api/v1/cardholders/CH-1002");
        // Nositelem oprávnění je karta — na držiteli je jen sjednocení.
        Assert.All(holder!.Cards, card => Assert.Contains("AL-03", card.AccessLevelIds));
        Assert.Contains("AL-03", holder.AccessLevelIds);
    }

    [Fact]
    public async Task DoorStatus_IsAvailable()
    {
        var status = await CreateClient().GetFromJsonAsync<DoorStatusDto>("/api/v1/doors/23");

        Assert.NotNull(status);
        Assert.Equal("23", status.Hid);
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("unlock")]
    [InlineData("pulse")]
    public async Task DoorCommands_Return204(string command)
    {
        var response = await CreateClient().PostAsync($"/api/v1/doors/23/{command}", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task DoorMode_RejectsUnknownValue()
    {
        var response = await CreateClient().PostAsJsonAsync("/api/v1/doors/23/mode",
            new DoorModeRequest((DoorMode)99));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Events_AreNotAvailableInMockMode()
    {
        var response = await CreateClient().GetAsync("/api/v1/events");

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    private sealed record CreatedCardHolder(string Id);
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
