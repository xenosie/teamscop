using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.Api.Tests;

/// <summary>
/// §14.2 leaderboard: one aggregate request instead of one request per staff member per minute,
/// which is what §15.2's hardware budget rules out.
/// </summary>
public class WorkSummaryFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly TrackingScenario _api;

    public WorkSummaryFlowTests(WebApplicationFactory<Program> factory)
        => _api = new TrackingScenario(factory.WithWebHostBuilder(_ => { }).CreateClient());

    [Fact]
    public async Task Leaderboard_RanksByHoursWorked_PagesAndExcludesSelf()
    {
        var (_, adminToken, companyToken) = await _api.SignupAdminAsync("Ranking Co");
        var (copId, copToken) = await _api.SignupStaffAsync(companyToken, "Cop");
        var (topId, topToken) = await _api.SignupStaffAsync(companyToken, "AAA Top");
        var (midId, midToken) = await _api.SignupStaffAsync(companyToken, "BBB Mid");

        var now = DateTimeOffset.UtcNow;
        await _api.IngestTimeTrackAsync(topToken, working: true, now.AddHours(-3), now.AddHours(-1));
        await _api.IngestTimeTrackAsync(midToken, working: true, now.AddHours(-3), now.AddHours(-2));
        // The viewer's own hours must never appear (§4.5).
        await _api.IngestTimeTrackAsync(copToken, working: true, now.AddHours(-6), now.AddHours(-1));
        await _api.GrantAsync(adminToken, copId, AuthorityPackageIds.ViewTimeTrack);

        var period = $"from={TrackingScenario.Iso(now.AddDays(-1))}&to={TrackingScenario.Iso(now.AddMinutes(1))}";

        using var full = await _api.GetJsonAsync($"/api/tracking/leaderboard?{period}", copToken);
        Assert.Equal(2, full.RootElement.GetProperty("total").GetInt32());
        var rows = full.RootElement.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.GetProperty("userId").GetGuid() == copId);
        Assert.Equal(1, rows[0].GetProperty("rank").GetInt32());
        Assert.Equal(topId, rows[0].GetProperty("userId").GetGuid());
        Assert.Equal(7200, rows[0].GetProperty("workedSeconds").GetInt64());
        Assert.Equal(2, rows[1].GetProperty("rank").GetInt32());
        Assert.Equal(midId, rows[1].GetProperty("userId").GetGuid());

        // Paging keeps the ranks it computed over the whole visible set.
        using var page2 = await _api.GetJsonAsync($"/api/tracking/leaderboard?{period}&page=1&pageSize=1", copToken);
        Assert.Equal(2, page2.RootElement.GetProperty("total").GetInt32());
        var second = page2.RootElement.GetProperty("rows").EnumerateArray().Single();
        Assert.Equal(2, second.GetProperty("rank").GetInt32());
        Assert.Equal(midId, second.GetProperty("userId").GetGuid());
    }

    [Fact]
    public async Task Leaderboard_RequiresTimeTrackPackage_AndBoundedPeriod()
    {
        var (_, adminToken, companyToken) = await _api.SignupAdminAsync("Gate Co");
        var (_, plainToken) = await _api.SignupStaffAsync(companyToken, "Plain");

        var now = DateTimeOffset.UtcNow;
        var period = $"from={TrackingScenario.Iso(now.AddDays(-1))}&to={TrackingScenario.Iso(now)}";

        Assert.Equal(HttpStatusCode.Forbidden,
            (await _api.GetAsync($"/api/tracking/leaderboard?{period}", plainToken)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await _api.GetAsync("/api/tracking/leaderboard", adminToken)).StatusCode);

        var tooWide = $"from={TrackingScenario.Iso(now.AddDays(-90))}&to={TrackingScenario.Iso(now)}";
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _api.GetAsync($"/api/tracking/leaderboard?{tooWide}", adminToken)).StatusCode);
    }

    [Fact]
    public async Task Leaderboard_ForTeamLeader_CoversOnlyTheirTeam()
    {
        var (_, adminToken, companyToken) = await _api.SignupAdminAsync("Leader Board Co");
        var (leaderId, leaderToken) = await _api.SignupStaffAsync(companyToken, "Leader");
        var (memberId, memberToken) = await _api.SignupStaffAsync(companyToken, "Member");
        var (outsiderId, outsiderToken) = await _api.SignupStaffAsync(companyToken, "Outsider");

        var now = DateTimeOffset.UtcNow;
        await _api.IngestTimeTrackAsync(memberToken, working: true, now.AddHours(-2), now.AddHours(-1));
        await _api.IngestTimeTrackAsync(outsiderToken, working: true, now.AddHours(-5), now.AddHours(-1));

        await _api.CreateTeamAsync(adminToken, "Alpha", leaderId, memberId);

        var period = $"from={TrackingScenario.Iso(now.AddDays(-1))}&to={TrackingScenario.Iso(now.AddMinutes(1))}";
        using var doc = await _api.GetJsonAsync($"/api/tracking/leaderboard?{period}", leaderToken);

        // A leader holds view_timetrack inherently, but only over their own team (§4.2).
        var rows = doc.RootElement.GetProperty("rows").EnumerateArray().ToList();
        Assert.Single(rows);
        Assert.Equal(memberId, rows[0].GetProperty("userId").GetGuid());
        Assert.DoesNotContain(rows, r => r.GetProperty("userId").GetGuid() == outsiderId);
        Assert.DoesNotContain(rows, r => r.GetProperty("userId").GetGuid() == leaderId);
    }
}
