using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Tests;

public class PoliceAuthorityFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public PoliceAuthorityFlowTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(_ => { }).CreateClient();
    }

    [Fact]
    public async Task PolicemanPackages_GateViewsTotpAndTeamManagement_CompanyWide()
    {
        var (adminToken, companyToken) = await SignupAdminAsync("Police Co");
        var (copId, copToken, _) = await SignupStaffAsync(companyToken, "Cop");
        var (targetId, targetToken, _) = await SignupStaffAsync(companyToken, "Target");
        var (otherId, _, _) = await SignupStaffAsync(companyToken, "OtherStaff");

        // Target emits timetrack + browser events
        await IngestAsync(targetToken, AgentEventTypes.TimeTrack, """{"state":"Working"}""");
        await IngestAsync(targetToken, AgentEventTypes.BrowserHistory, """{"url":"https://example.com"}""");
        await IngestAsync(targetToken, AgentEventTypes.ScreenshotMeta, """{"bytes":1234}""");

        // Grant only timetrack view
        using var setReq = Authed(HttpMethod.Put, $"/api/police/{copId}", adminToken);
        setReq.Content = JsonContent.Create(new { packages = new[] { AuthorityPackageIds.ViewTimeTrack } });
        (await _client.SendAsync(setReq)).EnsureSuccessStatusCode();

        using var meReq = Authed(HttpMethod.Get, "/api/police/me", copToken);
        var me = JsonDocument.Parse(await (await _client.SendAsync(meReq)).Content.ReadAsStringAsync());
        Assert.True(me.RootElement.GetProperty("isPoliceman").GetBoolean());
        Assert.Contains(me.RootElement.GetProperty("packages").EnumerateArray(),
            p => p.GetString() == AuthorityPackageIds.ViewTimeTrack);

        // Company-wide staff list
        using var staffReq = Authed(HttpMethod.Get, "/api/tracking/staff", copToken);
        var staffDoc = JsonDocument.Parse(await (await _client.SendAsync(staffReq)).Content.ReadAsStringAsync());
        Assert.True(staffDoc.RootElement.GetArrayLength() >= 3);

        // Timetrack visible for any staff; browser/screenshot filtered out
        using var ttReq = Authed(HttpMethod.Get, $"/api/tracking/events?staffUserId={targetId}&take=20", copToken);
        var ttDoc = JsonDocument.Parse(await (await _client.SendAsync(ttReq)).Content.ReadAsStringAsync());
        Assert.All(ttDoc.RootElement.EnumerateArray(), e =>
            Assert.Equal(AgentEventTypes.TimeTrack, e.GetProperty("eventType").GetString()));
        Assert.True(ttDoc.RootElement.GetArrayLength() >= 1);

        using var brReq = Authed(HttpMethod.Get,
            $"/api/tracking/events?staffUserId={targetId}&eventType={AgentEventTypes.BrowserHistory}", copToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(brReq)).StatusCode);

        // No USB package → cannot generate TOTP
        using var codeDeny = Authed(HttpMethod.Get, $"/api/lifecycle/totp/code/{targetId}", copToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(codeDeny)).StatusCode);

        // No team_management → cannot create teams
        using var teamDeny = Authed(HttpMethod.Post, "/api/teams", copToken);
        teamDeny.Content = JsonContent.Create(new { name = "Nope", leaderUserId = otherId });
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(teamDeny)).StatusCode);

        // Expand packages: USB + team management + browser
        using var expand = Authed(HttpMethod.Put, $"/api/police/{copId}", adminToken);
        expand.Content = JsonContent.Create(new
        {
            packages = new[]
            {
                AuthorityPackageIds.ViewTimeTrack,
                AuthorityPackageIds.ViewBrowserHistory,
                AuthorityPackageIds.UsbApproval,
                AuthorityPackageIds.TeamManagement
            }
        });
        (await _client.SendAsync(expand)).EnsureSuccessStatusCode();

        // Enroll TOTP as admin, generate as policeman with usb_approval
        using var enroll = Authed(HttpMethod.Post, "/api/lifecycle/totp/enroll", adminToken);
        enroll.Content = JsonContent.Create(new { staffUserId = targetId });
        var enrollDoc = JsonDocument.Parse(await (await _client.SendAsync(enroll)).Content.ReadAsStringAsync());
        var secret = enrollDoc.RootElement.GetProperty("secret").GetString()!;

        using var codeOk = Authed(HttpMethod.Get, $"/api/lifecycle/totp/code/{targetId}", copToken);
        var codeResp = await _client.SendAsync(codeOk);
        Assert.True(codeResp.IsSuccessStatusCode, await codeResp.Content.ReadAsStringAsync());
        var code = JsonDocument.Parse(await codeResp.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString()!;
        Assert.Equal(TotpGenerator.ComputeCode(secret), code);

        // Team management works company-wide
        using var teamOk = Authed(HttpMethod.Post, "/api/teams", copToken);
        teamOk.Content = JsonContent.Create(new { name = "CopTeam", leaderUserId = otherId });
        var teamResp = await _client.SendAsync(teamOk);
        Assert.True(teamResp.IsSuccessStatusCode, await teamResp.Content.ReadAsStringAsync());

        // Browser now allowed
        using var brOk = Authed(HttpMethod.Get,
            $"/api/tracking/events?staffUserId={targetId}&eventType={AgentEventTypes.BrowserHistory}", copToken);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(brOk)).StatusCode);

        // Revoke policeman
        using var revoke = Authed(HttpMethod.Delete, $"/api/police/{copId}", adminToken);
        (await _client.SendAsync(revoke)).EnsureSuccessStatusCode();
        using var after = Authed(HttpMethod.Get, $"/api/tracking/events?staffUserId={targetId}", copToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(after)).StatusCode);
    }

    private async Task IngestAsync(string token, string type, string payload)
    {
        using var ingest = Authed(HttpMethod.Post, "/api/ingest/batch", token);
        ingest.Content = JsonContent.Create(new IngestBatchRequest
        {
            Events =
            [
                new IngestEventDto
                {
                    ClientEventId = Guid.NewGuid(),
                    EventType = type,
                    OccurredAt = DateTimeOffset.UtcNow,
                    PayloadJson = payload
                }
            ]
        });
        (await _client.SendAsync(ingest)).EnsureSuccessStatusCode();
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
