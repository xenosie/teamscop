using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace Teamscop.Api.Tests;

/// <summary>
/// Guards the ConfigHub group split. Scoping the whole company group to management once cut
/// plain staff agents off from BusinessTimeUpdated — silently, because nothing asserted it.
///
/// Rule: company:{id} carries non-sensitive company settings and EVERY connection joins it.
///       company:{id}:mgmt carries privileged payloads and only admins / team_management join.
/// </summary>
public class ConfigHubGroupTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ConfigHubGroupTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(_ => { });
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task PlainStaffAgent_ReceivesBusinessTimeUpdated()
    {
        var (adminToken, companyToken) = await SignupAdminAsync();
        var (_, staffToken, _) = await SignupStaffAsync(companyToken);

        await using var staffConn = Hub(staffToken);
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        staffConn.On<object>("BusinessTimeUpdated", _ => received.TrySetResult(true));
        await staffConn.StartAsync();

        await DeclareTimeZoneAsync(adminToken, "Europe/Berlin");

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(
            completed == received.Task,
            "A plain staff agent must receive BusinessTimeUpdated — the agent subscribes to it "
            + "in ConfigRealtimeClient and has no other path to the company clock.");

        await staffConn.StopAsync();
    }

    [Fact]
    public async Task PlainStaff_DoesNotReceiveOrgStructureUpdated()
    {
        var (adminToken, companyToken) = await SignupAdminAsync();
        var (_, staffToken, _) = await SignupStaffAsync(companyToken);
        var (leaderId, _, _) = await SignupStaffAsync(companyToken);

        await using var staffConn = Hub(staffToken);
        var leaked = false;
        staffConn.On<object>("OrgStructureUpdated", _ => leaked = true);
        await staffConn.StartAsync();

        // Creating a team broadcasts the full org chart to the management group.
        using var req = Authed(HttpMethod.Post, "/api/teams", adminToken);
        req.Content = JsonContent.Create(new { name = "Team A", leaderUserId = leaderId });
        (await _client.SendAsync(req)).EnsureSuccessStatusCode();

        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.False(
            leaked,
            "Plain staff must not receive the org chart — GET /api/org/structure requires "
            + "team_management, so the push must be equally restricted.");

        await staffConn.StopAsync();
    }

    [Fact]
    public async Task Admin_ReceivesOrgStructureUpdated()
    {
        var (adminToken, companyToken) = await SignupAdminAsync();
        var (leaderId, _, _) = await SignupStaffAsync(companyToken);

        await using var adminConn = Hub(adminToken);
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        adminConn.On<object>("OrgStructureUpdated", _ => received.TrySetResult(true));
        await adminConn.StartAsync();

        using var req = Authed(HttpMethod.Post, "/api/teams", adminToken);
        req.Content = JsonContent.Create(new { name = "Team B", leaderUserId = leaderId });
        (await _client.SendAsync(req)).EnsureSuccessStatusCode();

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(completed == received.Task, "Admins must still receive org structure pushes.");

        await adminConn.StopAsync();
    }

    private HubConnection Hub(string token)
        => new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress!, "hubs/config"), o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

    private async Task DeclareTimeZoneAsync(string adminToken, string timeZoneId)
    {
        using var req = Authed(HttpMethod.Put, "/api/business-time", adminToken);
        req.Content = JsonContent.Create(new { timeZoneId });
        (await _client.SendAsync(req)).EnsureSuccessStatusCode();
    }

    private async Task<(string AccessToken, string CompanyToken)> SignupAdminAsync()
    {
        var device = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device), "deviceKey" },
            { new StringContent("Hub Co " + Guid.NewGuid().ToString("N")[..6]), "username" },
            { new StringContent(Guid.NewGuid().ToString("N")), "password" }
        };
        var resp = await _client.PostAsync("/api/auth/admin/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("accessToken").GetString()!,
            doc.RootElement.GetProperty("companyToken").GetString()!);
    }

    private async Task<(Guid Id, string AccessToken, string DeviceKey)> SignupStaffAsync(string companyToken)
    {
        var device = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device), "deviceKey" },
            { new StringContent("Staff" + Guid.NewGuid().ToString("N")[..6]), "username" },
            { new StringContent(Guid.NewGuid().ToString("N")), "password" },
            { new StringContent(companyToken), "companyToken" }
        };
        var resp = await _client.PostAsync("/api/auth/staff/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("user").GetProperty("id").GetGuid(),
            doc.RootElement.GetProperty("accessToken").GetString()!,
            device);
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }
}
