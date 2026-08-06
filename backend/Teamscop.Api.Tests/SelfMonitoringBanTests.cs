using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Tests;

public class SelfMonitoringBanTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public SelfMonitoringBanTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(_ => { }).CreateClient();
    }

    [Fact]
    public async Task LeaderAndPolice_CannotMonitorSelf_ButSelfTimeTrackAllowed()
    {
        var (adminToken, companyToken) = await SignupAdminAsync("Self Ban Co");
        var (leaderId, leaderToken, _) = await SignupStaffAsync(companyToken, "Leader");
        var (memberId, _, _) = await SignupStaffAsync(companyToken, "Member");
        var (copId, copToken, _) = await SignupStaffAsync(companyToken, "Cop");

        using var teamReq = Authed(HttpMethod.Post, "/api/teams", adminToken);
        teamReq.Content = JsonContent.Create(new { name = "T1", leaderUserId = leaderId });
        var teamResp = await _client.SendAsync(teamReq);
        teamResp.EnsureSuccessStatusCode();
        var teamId = JsonDocument.Parse(await teamResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("teamId").GetGuid();

        using var addMem = Authed(HttpMethod.Put, $"/api/teams/{teamId}/members", adminToken);
        addMem.Content = JsonContent.Create(new { memberUserIds = new[] { memberId } });
        (await _client.SendAsync(addMem)).EnsureSuccessStatusCode();

        using var setCop = Authed(HttpMethod.Put, $"/api/police/{copId}", adminToken);
        setCop.Content = JsonContent.Create(new
        {
            packages = new[]
            {
                AuthorityPackageIds.ViewTimeTrack,
                AuthorityPackageIds.ViewScreenshot,
                AuthorityPackageIds.ViewBrowserHistory
            }
        });
        (await _client.SendAsync(setCop)).EnsureSuccessStatusCode();

        // Lists exclude self
        using var leadList = Authed(HttpMethod.Get, "/api/tracking/staff", leaderToken);
        var leadDoc = JsonDocument.Parse(await (await _client.SendAsync(leadList)).Content.ReadAsStringAsync());
        Assert.DoesNotContain(leadDoc.RootElement.EnumerateArray(),
            e => e.GetProperty("userId").GetGuid() == leaderId);

        using var copList = Authed(HttpMethod.Get, "/api/tracking/staff", copToken);
        var copDoc = JsonDocument.Parse(await (await _client.SendAsync(copList)).Content.ReadAsStringAsync());
        Assert.DoesNotContain(copDoc.RootElement.EnumerateArray(),
            e => e.GetProperty("userId").GetGuid() == copId);

        // Events/self blocked
        using var selfEvents = Authed(HttpMethod.Get, $"/api/tracking/events?staffUserId={leaderId}&take=10", leaderToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(selfEvents)).StatusCode);

        using var copSelf = Authed(HttpMethod.Get, $"/api/tracking/events?staffUserId={copId}&take=10", copToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(copSelf)).StatusCode);

        // Own timetrack sticker still allowed
        var to = DateTimeOffset.UtcNow.AddMinutes(1);
        var from = to.AddHours(-24);
        using var selfTt = Authed(HttpMethod.Get,
            $"/api/tracking/timetrack?staffUserId={leaderId:D}&from={Uri.EscapeDataString(from.UtcDateTime.ToString("o"))}&to={Uri.EscapeDataString(to.UtcDateTime.ToString("o"))}",
            leaderToken);
        Assert.True((await _client.SendAsync(selfTt)).IsSuccessStatusCode);
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

    private async Task<(Guid Id, string AccessToken, string DeviceKey)> SignupStaffAsync(
        string companyToken, string name)
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
