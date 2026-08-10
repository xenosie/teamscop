using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace Teamscop.Api.Tests;

/// <summary>
/// Defect 3 — "even though one staff registered in business, in the admin side, cannot fetch it."
///
/// Two halves, and the suite had neither. Every existing test of <c>GET /api/tracking/staff</c>
/// authenticates as a team leader or a policeman (LeaderPolicemanScopeTests, SelfMonitoringBanTests,
/// TeamOrgFlowTests, PoliceAuthorityFlowTests) — not one uses an admin token, which is the only
/// token the owner ever holds. And nothing covered a staff member enrolling while a viewer is
/// already connected, which is the only way it ever happens: §2.3 has no approval step, so the
/// admin's app is open and idle at the moment the new machine appears.
/// </summary>
public class StaffDiscoveryTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public StaffDiscoveryTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(_ => { });
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task Admin_SeesAStaffMemberWhoRegisteredAfterTheAdminDid()
    {
        var (adminToken, companyToken) = await SignupAdminAsync();

        // A brand-new company: the admin is alone, and must not see themselves (§4.5).
        Assert.Empty(await ListVisibleStaffAsync(adminToken));

        var staffId = await SignupStaffAsync(companyToken);

        var visible = await ListVisibleStaffAsync(adminToken);
        Assert.Contains(staffId, visible);
        Assert.Single(visible);
    }

    [Fact]
    public async Task StaffSignup_PushesOrgStructureUpdated_ToAConnectedAdmin()
    {
        var (adminToken, companyToken) = await SignupAdminAsync();

        await using var adminConn = Hub(adminToken);
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        adminConn.On<JsonElement>("OrgStructureUpdated", org => received.TrySetResult(org));
        await adminConn.StartAsync();

        var staffId = await SignupStaffAsync(companyToken);

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(
            completed == received.Task,
            "A staff enrolment must push the org structure. Without it the desktop's staff "
            + "directory only ever loads at cold start, so an admin with the app already open "
            + "watches an empty dropdown indefinitely.");

        // The push must carry the new employee, not merely announce that something happened.
        var unassigned = (await received.Task).GetProperty("unassignedStaff");
        Assert.Contains(
            unassigned.EnumerateArray(),
            s => s.GetProperty("userId").GetGuid() == staffId);

        await adminConn.StopAsync();
    }

    [Fact]
    public async Task StaffSignup_PushesStaffRegistered_ToAPlainStaffConnection_ButNotTheOrgChart()
    {
        var (_, companyToken) = await SignupAdminAsync();
        var watcherToken = await SignupStaffTokenAsync(companyToken);

        await using var watcher = Hub(watcherToken);
        var orgChartLeaked = false;
        var nudged = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.On<JsonElement>("OrgStructureUpdated", _ => orgChartLeaked = true);
        watcher.On<JsonElement>("StaffRegistered", e => nudged.TrySetResult(e));
        await watcher.StartAsync();

        await SignupStaffAsync(companyToken);

        var completed = await Task.WhenAny(nudged.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(
            completed == nudged.Task,
            "A team leader and an approval-only policeman are deliberately outside the management "
            + "group, so the org chart can never reach them — but their own scoped roster just "
            + "changed and they need to know to re-read it.");

        // Contentless by design: whoever re-reads their roster gets it re-scoped by the server,
        // so this message can never widen anybody's reach.
        var payload = await nudged.Task;
        Assert.True(payload.TryGetProperty("companyId", out _));
        Assert.True(payload.TryGetProperty("structureVersion", out _));
        Assert.False(payload.TryGetProperty("username", out _));
        Assert.False(payload.TryGetProperty("unassignedStaff", out _));

        Assert.False(
            orgChartLeaked,
            "Plain staff must not receive the org chart — GET /api/org/structure requires "
            + "team_management, so the push must be equally restricted.");

        await watcher.StopAsync();
    }

    private HubConnection Hub(string token)
        => new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress!, "hubs/config"), o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

    private async Task<List<Guid>> ListVisibleStaffAsync(string token)
    {
        using var req = Authed(HttpMethod.Get, "/api/tracking/staff", token);
        var resp = await _client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("userId").GetGuid())
            .ToList();
    }

    private async Task<(string AccessToken, string CompanyToken)> SignupAdminAsync()
    {
        var device = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device), "deviceKey" },
            { new StringContent("Discover " + Guid.NewGuid().ToString("N")[..6]), "username" },
            { new StringContent("password123"), "password" }
        };
        var resp = await _client.PostAsync("/api/auth/admin/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("accessToken").GetString()!,
            doc.RootElement.GetProperty("companyToken").GetString()!);
    }

    private async Task<Guid> SignupStaffAsync(string companyToken)
        => (await SignupStaffFullAsync(companyToken)).Id;

    private async Task<string> SignupStaffTokenAsync(string companyToken)
        => (await SignupStaffFullAsync(companyToken)).AccessToken;

    private async Task<(Guid Id, string AccessToken)> SignupStaffFullAsync(string companyToken)
    {
        var device = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device), "deviceKey" },
            { new StringContent("Staff" + Guid.NewGuid().ToString("N")[..6]), "username" },
            { new StringContent("password123"), "password" },
            { new StringContent(companyToken), "companyToken" }
        };
        var resp = await _client.PostAsync("/api/auth/staff/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("user").GetProperty("id").GetGuid(),
            doc.RootElement.GetProperty("accessToken").GetString()!);
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }
}
