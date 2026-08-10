using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Teamscop.Api.Tests;

/// <summary>
/// §5 — the roster badge. Each staff member resolves to working (input within the 3-minute idle
/// window), rest (heartbeat present but idle) or offline (no recent heartbeat / PC off / agent not
/// running). Served on one call for the whole visible roster (§15.2).
/// </summary>
public sealed class StaffPresenceFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly TrackingScenario _api;

    public StaffPresenceFlowTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(_ => { });
        _api = new TrackingScenario(_factory.CreateClient());
    }

    [Fact]
    public async Task Presence_ClassifiesWorkingRestAndOffline_ForTheVisibleRoster()
    {
        var (_, adminToken, companyToken) = await _api.SignupAdminAsync("Presence Co");
        var (workingId, workingToken) = await _api.SignupStaffAsync(companyToken, "Worker");
        var (restId, restToken) = await _api.SignupStaffAsync(companyToken, "Rester");
        var (offlineId, _) = await _api.SignupStaffAsync(companyToken, "Absent");

        var now = DateTimeOffset.UtcNow;

        // Working: a timetrack window that just closed carrying worked time, plus a live heartbeat.
        await _api.IngestTimeTrackAsync(workingToken, working: true, now.AddSeconds(-60), now);
        await _api.IngestHeartbeatAsync(workingToken, now);

        // Rest: heartbeat present, but no recent input.
        await _api.IngestHeartbeatAsync(restToken, now);

        // Offline: never reported at all.

        using var doc = await GetJsonAsync("/api/tracking/presence", adminToken);
        var byUser = doc.RootElement.EnumerateArray()
            .ToDictionary(e => e.GetProperty("userId").GetGuid(), e => e.GetProperty("state").GetString());

        Assert.Equal("working", byUser[workingId]);
        Assert.Equal("rest", byUser[restId]);
        Assert.Equal("offline", byUser[offlineId]);
    }

    [Fact]
    public async Task Presence_IsScopedLikeTheRoster_ATeamLeaderSeesOnlyTheirTeam()
    {
        var (_, adminToken, companyToken) = await _api.SignupAdminAsync("Scoped Co");
        var (leaderId, leaderToken) = await _api.SignupStaffAsync(companyToken, "Leader");
        var (memberId, _) = await _api.SignupStaffAsync(companyToken, "Member");
        var (outsiderId, _) = await _api.SignupStaffAsync(companyToken, "Outsider");

        await _api.CreateTeamAsync(adminToken, "Alpha", leaderId, memberId);

        using var doc = await GetJsonAsync("/api/tracking/presence", leaderToken);
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("userId").GetGuid()).ToList();

        Assert.Contains(memberId, ids);
        Assert.DoesNotContain(outsiderId, ids); // not on the leader's team
        Assert.DoesNotContain(leaderId, ids);   // never yourself (§4.5)
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _factory.CreateClient().SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonDocument.Parse(body);
    }
}
