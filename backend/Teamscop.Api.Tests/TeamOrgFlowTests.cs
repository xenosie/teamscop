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
