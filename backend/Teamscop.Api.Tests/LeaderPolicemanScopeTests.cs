using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.Api.Tests;

/// <summary>
/// §4.2 / §4.3 — a team leader sees their OWN TEAM only, and that must survive being granted an
/// approval package.
///
/// The escalation this guards: company-wide reach was decided by one flag that treated
/// usb_approval as company-wide (correct for the roster — you must pick whom to issue a code for)
/// and then reused it to gate tracking data. A team leader carries inherent view packages scoped
/// to their team, so granting that leader USB approval — a routine, low-privilege delegation —
/// silently promoted those views to the whole company and handed them every employee's
/// screenshots and browsing history.
/// </summary>
public class LeaderPolicemanScopeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public LeaderPolicemanScopeTests(WebApplicationFactory<Program> factory)
        => _client = factory.WithWebHostBuilder(_ => { }).CreateClient();

    [Fact]
    public async Task LeaderWithUsbApproval_StillSeesOnlyTheirOwnTeam()
    {
        var (adminToken, companyToken) = await SignupAdminAsync();
        var (leaderId, leaderToken, _) = await SignupStaffAsync(companyToken);
        var (memberId, _, _) = await SignupStaffAsync(companyToken);
        var (outsiderId, _, _) = await SignupStaffAsync(companyToken);

        await CreateTeamAsync(adminToken, leaderId, memberId);

        // The routine delegation that used to escalate: let the leader unlock USB sticks.
        await GrantPackagesAsync(adminToken, leaderId, AuthorityPackageIds.UsbApproval);

        var visible = await ListVisibleStaffAsync(leaderToken);
        Assert.Contains(memberId, visible);
        Assert.DoesNotContain(outsiderId, visible);

        // And the data itself, not just the list. A leader's inherent reach is timetrack AND
        // screenshots, scoped to their own team; the outsider stays forbidden on both.
        Assert.Equal(HttpStatusCode.OK, await ScreenshotStatusAsync(leaderToken, memberId));
        Assert.Equal(HttpStatusCode.Forbidden, await ScreenshotStatusAsync(leaderToken, outsiderId));
        Assert.Equal(HttpStatusCode.OK, await TimeTrackStatusAsync(leaderToken, memberId));
        Assert.Equal(HttpStatusCode.Forbidden, await TimeTrackStatusAsync(leaderToken, outsiderId));
    }

    [Fact]
    public async Task LeaderWithGrantedViewPackage_DoesSeeTheWholeCompany()
    {
        var (adminToken, companyToken) = await SignupAdminAsync();
        var (leaderId, leaderToken, _) = await SignupStaffAsync(companyToken);
        var (memberId, _, _) = await SignupStaffAsync(companyToken);
        var (outsiderId, _, _) = await SignupStaffAsync(companyToken);

        await CreateTeamAsync(adminToken, leaderId, memberId);

        // The opposite case must still work: an explicitly granted view package IS company-wide
        // (§4.3), so the narrowing must not break a deliberate promotion.
        await GrantPackagesAsync(adminToken, leaderId, AuthorityPackageIds.ViewScreenshot);

        var visible = await ListVisibleStaffAsync(leaderToken);
        Assert.Contains(memberId, visible);
        Assert.Contains(outsiderId, visible);
        Assert.Equal(HttpStatusCode.OK, await ScreenshotStatusAsync(leaderToken, outsiderId));
    }

    /// <summary>
    /// The leader-AND-policeman case, pinned from both directions. A granted view package reaches
    /// company-wide; the leader's inherent timetrack reaches exactly the led team. The old flat
    /// effective-set collapsed the two scopes, so granting this person view_screenshot silently
    /// promoted their team-scoped timetrack to every employee in the company.
    /// </summary>
    [Fact]
    public async Task LeaderWhoIsAlsoPoliceman_KeepsEachPackageInItsOwnScope()
    {
        var (adminToken, companyToken) = await SignupAdminAsync();
        var (leaderId, leaderToken, _) = await SignupStaffAsync(companyToken);
        var (memberId, _, _) = await SignupStaffAsync(companyToken);
        var (outsiderId, _, _) = await SignupStaffAsync(companyToken);

        await CreateTeamAsync(adminToken, leaderId, memberId);
        await GrantPackagesAsync(adminToken, leaderId, AuthorityPackageIds.ViewScreenshot);

        // The granted package: screenshots, company-wide. Works for both member and outsider.
        Assert.Equal(HttpStatusCode.OK, await ScreenshotStatusAsync(leaderToken, memberId));
        Assert.Equal(HttpStatusCode.OK, await ScreenshotStatusAsync(leaderToken, outsiderId));

        // The inherent package: timetrack, led team ONLY. The outsider row is the escalation the
        // flat set allowed — it must stay forbidden no matter what else is granted.
        Assert.Equal(HttpStatusCode.OK, await TimeTrackStatusAsync(leaderToken, memberId));
        Assert.Equal(HttpStatusCode.Forbidden, await TimeTrackStatusAsync(leaderToken, outsiderId));
    }

    [Fact]
    public async Task ApprovalOnlyPoliceman_CanPickAnyStaffForCodes_ButSeesNoData()
    {
        var (adminToken, companyToken) = await SignupAdminAsync();
        var (copId, copToken, _) = await SignupStaffAsync(companyToken);
        var (staffId, _, _) = await SignupStaffAsync(companyToken);

        await GrantPackagesAsync(adminToken, copId, AuthorityPackageIds.UsbApproval);

        // The roster must stay company-wide — otherwise they cannot choose whom to issue a code
        // for, which is the entire job of the package.
        using var roster = Authed(HttpMethod.Get, "/api/lifecycle/totp/staff", copToken);
        var rosterResp = await _client.SendAsync(roster);
        Assert.Equal(HttpStatusCode.OK, rosterResp.StatusCode);
        Assert.Contains(staffId.ToString(), await rosterResp.Content.ReadAsStringAsync());

        // But no tracking data.
        Assert.Equal(HttpStatusCode.Forbidden, await ScreenshotStatusAsync(copToken, staffId));
    }

    private async Task<HttpStatusCode> ScreenshotStatusAsync(string token, Guid staffUserId)
    {
        using var req = Authed(
            HttpMethod.Get, $"/api/tracking/screenshots?staffUserId={staffUserId:D}", token);
        return (await _client.SendAsync(req)).StatusCode;
    }

    private async Task<HttpStatusCode> TimeTrackStatusAsync(string token, Guid staffUserId)
    {
        var from = DateTimeOffset.UtcNow.AddHours(-1).ToString("O");
        var to = DateTimeOffset.UtcNow.ToString("O");
        using var req = Authed(
            HttpMethod.Get,
            $"/api/tracking/timetrack?staffUserId={staffUserId:D}&from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}",
            token);
        return (await _client.SendAsync(req)).StatusCode;
    }

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

    private async Task CreateTeamAsync(string adminToken, Guid leaderId, Guid memberId)
    {
        using var create = Authed(HttpMethod.Post, "/api/teams", adminToken);
        create.Content = JsonContent.Create(
            new { name = "T" + Guid.NewGuid().ToString("N")[..6], leaderUserId = leaderId });
        var resp = await _client.SendAsync(create);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var teamId = doc.RootElement.GetProperty("teamId").GetGuid();

        using var add = Authed(HttpMethod.Put, $"/api/teams/{teamId:D}/members", adminToken);
        add.Content = JsonContent.Create(new { memberUserIds = new[] { memberId } });
        (await _client.SendAsync(add)).EnsureSuccessStatusCode();
    }

    private async Task GrantPackagesAsync(string adminToken, Guid staffUserId, params string[] packages)
    {
        using var req = Authed(HttpMethod.Put, $"/api/police/{staffUserId:D}", adminToken);
        req.Content = JsonContent.Create(new { packages });
        (await _client.SendAsync(req)).EnsureSuccessStatusCode();
    }

    private async Task<(string AccessToken, string CompanyToken)> SignupAdminAsync()
    {
        var device = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device), "deviceKey" },
            { new StringContent("Co " + Guid.NewGuid().ToString("N")[..6]), "username" },
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
            { new StringContent("S" + Guid.NewGuid().ToString("N")[..8]), "username" },
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
