using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace Teamscop.Api.Tests;

public class ConfigHubAuthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ConfigHubAuthTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(_ => { });
    }

    [Fact]
    public async Task ConfigHub_AcceptsJwtViaAccessTokenQuery()
    {
        var client = _factory.CreateClient();
        var token = await SignupAdminAsync(client, "Hub Auth Co");

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress!, "hubs/config"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .WithAutomaticReconnect()
            .Build();

        await connection.StartAsync();
        Assert.Equal(HubConnectionState.Connected, connection.State);
        await connection.StopAsync();
    }

    [Fact]
    public async Task ConfigHub_RejectsAnonymous()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress!, "hubs/config"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(async () => await connection.StartAsync());
    }

    private static async Task<string> SignupAdminAsync(HttpClient client, string name)
    {
        var device = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device), "deviceKey" },
            { new StringContent(name), "username" },
            { new StringContent("password123"), "password" }
        };
        var resp = await client.PostAsync("/api/auth/admin/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }
}
