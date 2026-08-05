using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Teamscop.Api.Tests;

public class TeamOrgFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public TeamOrgFlowTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(_ => { }).CreateClient();
    }

    [Fact]
    public async Task AdminBuildsTeam_LeaderHasNoAutoTrackingVisibility()
    {
        var (adminToken, companyToken) = await SignupAdminAsync("Org Co");
        var (leaderId, leaderToken, _) = await SignupStaffAsync(companyToken, "Leader");
        var (memberId, memberToken, _) = await SignupStaffAsync(companyToken, "Member");
        var (otherId, _, _) = await SignupStaffAsync(companyToken, "Other");

        using var createReq = Authed(HttpMethod.Post, "/api/teams", adminToken);
        createReq.Content = JsonContent.Create(new { name = "Alpha", leaderUserId = leaderId });
        var createResp = await _client.SendAsync(createReq);
        createResp.EnsureSuccessStatusCode();
        var teamId = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("teamId").GetGuid();

        using var memReq = Authed(HttpMethod.Put, $"/api/teams/{teamId}/members", adminToken);
        memReq.Content = JsonContent.Create(new { memberUserIds = new[] { memberId } });
        (await _client.SendAsync(memReq)).EnsureSuccessStatusCode();

        // Team leader without packages cannot see member tracking (packages-only model).
        using var visibleReq = Authed(HttpMethod.Get, "/api/tracking/staff", leaderToken);
        var visibleResp = await _client.SendAsync(visibleReq);
        visibleResp.EnsureSuccessStatusCode();
        Assert.Equal(0, JsonDocument.Parse(await visibleResp.Content.ReadAsStringAsync()).RootElement.GetArrayLength());

        using var eventsReq = Authed(HttpMethod.Get, $"/api/tracking/events?staffUserId={memberId}", leaderToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(eventsReq)).StatusCode);

        using var memberDeny = Authed(HttpMethod.Get, $"/api/tracking/events?staffUserId={leaderId}", memberToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(memberDeny)).StatusCode);

        using var orgReq = Authed(HttpMethod.Get, "/api/org/structure", adminToken);
        var orgDoc = JsonDocument.Parse(await (await _client.SendAsync(orgReq)).Content.ReadAsStringAsync());
        Assert.True(orgDoc.RootElement.GetProperty("structureVersion").GetInt64() >= 1);
        Assert.Contains(orgDoc.RootElement.GetProperty("unassignedStaff").EnumerateArray(),
            e => e.GetProperty("userId").GetGuid() == otherId);

        using var placeReq = Authed(HttpMethod.Get, "/api/org/me", leaderToken);
        var place = JsonDocument.Parse(await (await _client.SendAsync(placeReq)).Content.ReadAsStringAsync());
        Assert.Equal("leader", place.RootElement.GetProperty("placement").GetString());
    }

    [Fact]
    public async Task CreateTeam_WithoutLeader_ThenClearAndSwitchLeader()
    {
        var (adminToken, companyToken) = await SignupAdminAsync("LeaderOps Co");
        var (aId, _, _) = await SignupStaffAsync(companyToken, "Alice");
        var (bId, _, _) = await SignupStaffAsync(companyToken, "Bob");
        var (cId, _, _) = await SignupStaffAsync(companyToken, "Carol");

        using var createReq = Authed(HttpMethod.Post, "/api/teams", adminToken);
        createReq.Content = JsonContent.Create(new { name = "NoLeaderYet" });
        var createResp = await _client.SendAsync(createReq);
        var createBody = await createResp.Content.ReadAsStringAsync();
        Assert.True(createResp.IsSuccessStatusCode, createBody);
        using var createDoc = JsonDocument.Parse(createBody);
        Assert.Equal(JsonValueKind.Null, createDoc.RootElement.GetProperty("leader").ValueKind);
        var teamId = createDoc.RootElement.GetProperty("teamId").GetGuid();

        using var setLeaderReq = Authed(HttpMethod.Put, $"/api/teams/{teamId}", adminToken);
        setLeaderReq.Content = JsonContent.Create(new { leaderUserId = aId });
        (await _client.SendAsync(setLeaderReq)).EnsureSuccessStatusCode();

        using var memReq = Authed(HttpMethod.Put, $"/api/teams/{teamId}/members", adminToken);
        memReq.Content = JsonContent.Create(new { memberUserIds = new[] { bId } });
        (await _client.SendAsync(memReq)).EnsureSuccessStatusCode();

        // Promote member B to leader (steal/clear A).
        using var switchReq = Authed(HttpMethod.Put, $"/api/teams/{teamId}", adminToken);
        switchReq.Content = JsonContent.Create(new { leaderUserId = bId });
        var switchResp = await _client.SendAsync(switchReq);
        var switchBody = await switchResp.Content.ReadAsStringAsync();
        Assert.True(switchResp.IsSuccessStatusCode, switchBody);
        using var switchDoc = JsonDocument.Parse(switchBody);
        Assert.Equal(bId, switchDoc.RootElement.GetProperty("leader").GetProperty("userId").GetGuid());
        Assert.DoesNotContain(
            switchDoc.RootElement.GetProperty("members").EnumerateArray(),
            e => e.GetProperty("userId").GetGuid() == bId);

        // Steal leader from another team: C leads Team2, then becomes leader of Team1.
        using var create2 = Authed(HttpMethod.Post, "/api/teams", adminToken);
        create2.Content = JsonContent.Create(new { name = "OtherTeam", leaderUserId = cId });
        var t2Resp = await _client.SendAsync(create2);
        t2Resp.EnsureSuccessStatusCode();
        var team2Id = JsonDocument.Parse(await t2Resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("teamId").GetGuid();

        using var steal = Authed(HttpMethod.Put, $"/api/teams/{teamId}", adminToken);
        steal.Content = JsonContent.Create(new { leaderUserId = cId });
        (await _client.SendAsync(steal)).EnsureSuccessStatusCode();

        using var orgReq = Authed(HttpMethod.Get, "/api/org/structure", adminToken);
        var org = JsonDocument.Parse(await (await _client.SendAsync(orgReq)).Content.ReadAsStringAsync());
        var teams = org.RootElement.GetProperty("teams").EnumerateArray().ToList();
        var t1 = Assert.Single(teams, t => t.GetProperty("teamId").GetGuid() == teamId);
        var t2 = Assert.Single(teams, t => t.GetProperty("teamId").GetGuid() == team2Id);
        Assert.Equal(cId, t1.GetProperty("leader").GetProperty("userId").GetGuid());
        Assert.Equal(JsonValueKind.Null, t2.GetProperty("leader").ValueKind);

        using var clearReq = Authed(HttpMethod.Put, $"/api/teams/{teamId}", adminToken);
        clearReq.Content = JsonContent.Create(new { clearLeader = true });
        var clearResp = await _client.SendAsync(clearReq);
        clearResp.EnsureSuccessStatusCode();
        using var clearDoc = JsonDocument.Parse(await clearResp.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, clearDoc.RootElement.GetProperty("leader").ValueKind);

        using var orgAfterClear = Authed(HttpMethod.Get, "/api/org/structure", adminToken);
        var pool = JsonDocument.Parse(await (await _client.SendAsync(orgAfterClear)).Content.ReadAsStringAsync());
        Assert.Contains(pool.RootElement.GetProperty("unassignedStaff").EnumerateArray(),
            e => e.GetProperty("userId").GetGuid() == aId);
        Assert.Contains(pool.RootElement.GetProperty("unassignedStaff").EnumerateArray(),
            e => e.GetProperty("userId").GetGuid() == cId);
    }

    private async Task<(string AccessToken, string CompanyToken)> SignupAdminAsync(string name)
    {
        var device = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device), "deviceKey" },
            { new StringContent(name), "username" },
            { new StringContent("password123"), "password" }
        };
        var resp = await _client.PostAsync("/api/auth/admin/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("accessToken").GetString()!,
            doc.RootElement.GetProperty("companyToken").GetString()!);
    }

    private async Task<(Guid Id, string AccessToken, string DeviceKey)> SignupStaffAsync(string companyToken, string name)
    {
        var device = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device), "deviceKey" },
            { new StringContent(name), "username" },
            { new StringContent("password123"), "password" },
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
