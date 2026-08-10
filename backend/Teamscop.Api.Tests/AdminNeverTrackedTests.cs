using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Teamscop.Api.Data;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Tests;

/// <summary>
/// Defect 8 — "admin should be never tracked. i think you are tracking admin."
///
/// The owner was right, and the database could not show it: the installer deploys the capturing
/// service to every PC including the admin's, so an admin machine captures into its local vault
/// and outbox right now. The only reason production holds zero admin rows is that the agent's
/// upload sits behind a token check it never reached — luck, not design. Nothing on the server
/// stopped an admin token writing timetrack, screenshots and browsing against the admin's own
/// account; every READ path filters <c>Role == Staff</c>, so those rows would have been stored,
/// invisible everywhere, and quietly deleted 30 days later.
///
/// §4.5 is a storage rule here, not a display rule. These lock it in by construction.
/// </summary>
public class AdminNeverTrackedTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public AdminNeverTrackedTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(_ => { });
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task AdminToken_CannotIngest_AndWritesNothing()
    {
        var (adminToken, _, _) = await SignupAdminAsync();
        var adminId = await MyUserIdAsync(adminToken);

        var resp = await IngestAsync(adminToken, TimeTrackEvent());
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.AgentEvents.AsNoTracking().Where(e => e.UserId == adminId).ToListAsync());
    }

    [Fact]
    public async Task TheSameBatch_FromAStaffToken_IsAccepted()
    {
        var (_, companyToken, _) = await SignupAdminAsync();
        var staff = await SignupStaffAsync(companyToken);

        // The guard must reject a ROLE, not a payload — otherwise it is just a broken ingest.
        var resp = await IngestAsync(staff.Token, TimeTrackEvent());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.NotEmpty(await db.AgentEvents.AsNoTracking().Where(e => e.UserId == staff.Id).ToListAsync());
    }

    [Fact]
    public async Task AdminHeartbeat_IsAcceptedButRecordsNoLiveness()
    {
        var (adminToken, _, _) = await SignupAdminAsync();
        var adminId = await MyUserIdAsync(adminToken);

        // Not a 403: an admin PC running an older agent would sit in a permanent error loop for a
        // call whose correct outcome is simply that nothing is stored. LastHeartbeatAt IS
        // monitoring — it is what §14.1's "who is working now" reads.
        using var req = Authed(HttpMethod.Post, "/api/lifecycle/heartbeat", adminToken);
        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var admin = await db.Users.AsNoTracking().FirstAsync(u => u.Id == adminId);
        Assert.Null(admin.LastHeartbeatAt);
        Assert.Null(admin.LastSeenAt);
        Assert.Null(admin.LastOnline);
    }

    [Fact]
    public async Task AnAdminNeverAppearsInAnyStaffList_IncludingTheirOwn()
    {
        var (adminToken, companyToken, _) = await SignupAdminAsync();
        var adminId = await MyUserIdAsync(adminToken);
        var staff = await SignupStaffAsync(companyToken);

        // Their own roster (§4.5 self-exclusion)…
        Assert.DoesNotContain(adminId, await ListVisibleStaffAsync(adminToken));

        // …and, since a leader or policeman could otherwise pick them, everyone else's too.
        await GrantPackagesAsync(adminToken, staff.Id, AuthorityPackageIds.ViewTimeTrack);
        var copView = await ListVisibleStaffAsync(staff.Token);
        Assert.DoesNotContain(adminId, copView);
    }

    [Fact]
    public async Task AdminData_CannotBeReadEvenByAPolicemanWhoAsksForItDirectly()
    {
        var (adminToken, companyToken, _) = await SignupAdminAsync();
        var adminId = await MyUserIdAsync(adminToken);
        var cop = await SignupStaffAsync(companyToken);
        await GrantPackagesAsync(
            adminToken, cop.Id, AuthorityPackageIds.ViewTimeTrack, AuthorityPackageIds.ViewScreenshot);

        using var req = Authed(
            HttpMethod.Get, $"/api/tracking/events?staffUserId={adminId:D}&take=10", cop.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(req)).StatusCode);
    }

    private static IngestBatchRequest TimeTrackEvent() => new()
    {
        Events =
        [
            new IngestEventDto
            {
                ClientEventId = Guid.NewGuid(),
                EventType = AgentEventTypes.TimeTrack,
                OccurredAt = DateTimeOffset.UtcNow,
                PayloadJson = """{"state":"Working"}"""
            }
        ]
    };

    private async Task<HttpResponseMessage> IngestAsync(string token, IngestBatchRequest batch)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/batch")
        {
            Content = JsonContent.Create(batch)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(req);
    }

    private async Task GrantPackagesAsync(string adminToken, Guid staffId, params string[] packages)
    {
        using var req = Authed(HttpMethod.Put, $"/api/police/{staffId}", adminToken);
        req.Content = JsonContent.Create(new { packages });
        (await _client.SendAsync(req)).EnsureSuccessStatusCode();
    }

    private async Task<List<Guid>> ListVisibleStaffAsync(string token)
    {
        using var req = Authed(HttpMethod.Get, "/api/tracking/staff", token);
        var resp = await _client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.EnumerateArray().Select(e => e.GetProperty("userId").GetGuid()).ToList();
    }

    private async Task<Guid> MyUserIdAsync(string token)
    {
        using var req = Authed(HttpMethod.Get, "/api/auth/me", token);
        var resp = await _client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<(string AccessToken, string CompanyToken, string DeviceKey)> SignupAdminAsync()
    {
        var device = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device), "deviceKey" },
            { new StringContent("NoTrack " + Guid.NewGuid().ToString("N")[..6]), "username" },
            { new StringContent("password123"), "password" }
        };
        var resp = await _client.PostAsync("/api/auth/admin/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("accessToken").GetString()!,
            doc.RootElement.GetProperty("companyToken").GetString()!,
            device);
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
