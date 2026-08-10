using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Teamscop.Api.Tests;

/// <summary>
/// <c>GET /api/org/me</c> answered 400 for every non-admin caller in production, on every poll,
/// forever — <c>TeamMember → Team → Members</c> is a cycle, and EF rejects a cycle in a no-tracking
/// query at COMPILATION time, before the predicate is ever evaluated. An admin never saw it because
/// admins return before that query runs, which is exactly why it looked healthy in the admin app.
///
/// The consequence was not merely log noise: the desktop feeds this response into the authority
/// state, so <c>isTeamLeader</c> never arrived and a team leader could never be shown their team
/// workspace (§4.2, §4.6). No test in the suite had ever called this route.
/// </summary>
public class OrgPlacementTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public OrgPlacementTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(_ => { }).CreateClient();
    }

    [Fact]
    public async Task UnassignedStaff_GetsAPlacement_NotA400()
    {
        var (_, companyToken) = await SignupAdminAsync();
        var staff = await SignupStaffAsync(companyToken);

        var me = await PlacementAsync(staff.Token);
        Assert.Equal("unassigned", me.GetProperty("placement").GetString());
        Assert.False(me.GetProperty("isTeamLeader").GetBoolean());
    }

    [Fact]
    public async Task TeamMember_GetsTheirTeam()
    {
        var (adminToken, companyToken) = await SignupAdminAsync();
        var leader = await SignupStaffAsync(companyToken);
        var member = await SignupStaffAsync(companyToken);
        var teamId = await CreateTeamAsync(adminToken, "Placement A", leader.Id);
        await SetMembersAsync(adminToken, teamId, member.Id);

        var me = await PlacementAsync(member.Token);
        Assert.Equal("member", me.GetProperty("placement").GetString());
        Assert.False(me.GetProperty("isTeamLeader").GetBoolean());
        Assert.Equal(teamId, me.GetProperty("teamId").GetGuid());

        // The team graph must still come back whole — dropping the cyclic Include must not have
        // dropped the data the workspace renders from.
        var team = me.GetProperty("team");
        Assert.Equal(leader.Id, team.GetProperty("leader").GetProperty("userId").GetGuid());
        Assert.Contains(
            team.GetProperty("members").EnumerateArray(),
            m => m.GetProperty("userId").GetGuid() == member.Id);
    }

    [Fact]
    public async Task TeamLeader_IsToldTheyLeadATeam()
    {
        var (adminToken, companyToken) = await SignupAdminAsync();
        var leader = await SignupStaffAsync(companyToken);
        var teamId = await CreateTeamAsync(adminToken, "Placement B", leader.Id);

        var me = await PlacementAsync(leader.Token);
        Assert.Equal("leader", me.GetProperty("placement").GetString());
        Assert.True(me.GetProperty("isTeamLeader").GetBoolean());
        Assert.Equal(teamId, me.GetProperty("teamId").GetGuid());
    }

    [Fact]
    public async Task Admin_IsPlacedAsAdmin()
    {
        var (adminToken, _) = await SignupAdminAsync();

        var me = await PlacementAsync(adminToken);
        Assert.Equal("admin", me.GetProperty("placement").GetString());
        Assert.False(me.GetProperty("isTeamLeader").GetBoolean());
    }

    private async Task<JsonElement> PlacementAsync(string token)
    {
        using var req = Authed(HttpMethod.Get, "/api/org/me", token);
        var resp = await _client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"/api/org/me answered {(int)resp.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<Guid> CreateTeamAsync(string adminToken, string name, Guid leaderId)
    {
        using var req = Authed(HttpMethod.Post, "/api/teams", adminToken);
        req.Content = JsonContent.Create(new { name = name + Guid.NewGuid().ToString("N")[..6], leaderUserId = leaderId });
        var resp = await _client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("teamId").GetGuid();
    }

    private async Task SetMembersAsync(string adminToken, Guid teamId, params Guid[] memberIds)
    {
        using var req = Authed(HttpMethod.Put, $"/api/teams/{teamId}/members", adminToken);
        req.Content = JsonContent.Create(new { memberUserIds = memberIds });
        (await _client.SendAsync(req)).EnsureSuccessStatusCode();
    }

    private async Task<(string AccessToken, string CompanyToken)> SignupAdminAsync()
    {
        var device = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device), "deviceKey" },
            { new StringContent("Placement " + Guid.NewGuid().ToString("N")[..6]), "username" },
            { new StringContent("password123"), "password" }
        };
        var resp = await _client.PostAsync("/api/auth/admin/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("accessToken").GetString()!,
            doc.RootElement.GetProperty("companyToken").GetString()!);
    }

    private async Task<(Guid Id, string Token)> SignupStaffAsync(string companyToken)
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
