using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Teamscop.Api.Data;
using Teamscop.Api.Options;
using Teamscop.Api.Services;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Tests;

/// <summary>
/// The behaviour only a real database can show: unique indexes that actually reject, and
/// <c>ExecuteDeleteAsync</c>, which the in-memory provider throws on. Each of these covers a bug
/// the rest of the suite is structurally unable to catch (C1).
/// </summary>
public sealed class PersistenceFlowTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    [PostgresFact]
    public async Task ConcurrentSignupsOnOneDeviceKey_LeaveExactlyOneAccount_AndNever500()
    {
        var client = factory.CreateClient();
        var deviceKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        // §1.3 — one machine, one account. The pre-check narrows the window; the unique index is
        // the guard. Losing the race is a 400, not the unlogged 500 it used to be (B7).
        var attempts = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(i => SignupAsync(client, deviceKey, $"Racer{i}")));

        Assert.Equal(1, attempts.Count(s => s == HttpStatusCode.OK));
        Assert.All(attempts.Where(s => s != HttpStatusCode.OK), s => Assert.Equal(HttpStatusCode.BadRequest, s));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Users.CountAsync(u => u.DeviceKey == deviceKey));
    }

    [PostgresFact]
    public async Task PromotingALeaderIntoMembership_ClearsTheLeadership_AndSurvivesARepeat()
    {
        var api = new TrackingScenario(factory.CreateClient());
        var (_, adminToken, companyToken) = await api.SignupAdminAsync("Org Persist Co");
        var (leaderId, _) = await api.SignupStaffAsync(companyToken, "Leader");
        var (memberId, _) = await api.SignupStaffAsync(companyToken, "Member");

        var alpha = await api.CreateTeamAsync(adminToken, "Alpha", leaderId);
        using var beta = await api.SendJsonAsync(HttpMethod.Post, "/api/teams", adminToken,
            new { name = "Beta", leaderUserId = memberId });
        var betaId = beta.RootElement.GetProperty("teamId").GetGuid();

        // B6 — the leadership clear used to sit outside the SaveChanges that follows it, so this
        // demotion was silently discarded. teams.LeaderUserId is UNIQUE, so a half-applied move
        // now fails loudly rather than leaving one person leading two teams.
        using var first = await api.SendJsonAsync(HttpMethod.Post, $"/api/teams/{betaId:D}/members", adminToken,
            new { staffUserId = leaderId });
        Assert.Contains(first.RootElement.GetProperty("members").EnumerateArray(),
            m => m.GetProperty("userId").GetGuid() == leaderId);

        // Re-adding an existing member must still persist everything else the call touched.
        using var again = await api.SendJsonAsync(HttpMethod.Post, $"/api/teams/{betaId:D}/members", adminToken,
            new { staffUserId = leaderId });
        Assert.Single(again.RootElement.GetProperty("members").EnumerateArray());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null(await db.Teams.Where(t => t.Id == alpha).Select(t => t.LeaderUserId).SingleAsync());
        Assert.Equal(betaId, await db.TeamMembers.Where(m => m.StaffUserId == leaderId)
            .Select(m => m.TeamId).SingleAsync());
    }

    [PostgresFact]
    public async Task ReplacingAMemberList_ThatOverlapsTheOldOne_DoesNotCollideOnThePrimaryKey()
    {
        var api = new TrackingScenario(factory.CreateClient());
        var (_, adminToken, companyToken) = await api.SignupAdminAsync("Roster Co");
        var (leaderId, _) = await api.SignupStaffAsync(companyToken, "Boss");
        var (aId, _) = await api.SignupStaffAsync(companyToken, "Ann");
        var (bId, _) = await api.SignupStaffAsync(companyToken, "Bob");
        var (cId, _) = await api.SignupStaffAsync(companyToken, "Cal");

        var teamId = await api.CreateTeamAsync(adminToken, "Roster", leaderId, aId, bId);

        // Bob is in both lists, so the replace stages a delete and an insert on one composite key
        // inside a single SaveChanges — the in-memory provider does not enforce that key.
        using var replaced = await api.SendJsonAsync(HttpMethod.Put, $"/api/teams/{teamId:D}/members", adminToken,
            new { memberUserIds = new[] { bId, cId } });
        var members = replaced.RootElement.GetProperty("members").EnumerateArray()
            .Select(m => m.GetProperty("userId").GetGuid())
            .ToHashSet();
        Assert.Equal([bId, cId], members);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.TeamMembers.AsNoTracking().Where(m => m.StaffUserId == aId).ToListAsync());
    }

    [PostgresFact]
    public async Task Retention_PrunesPastASingleBatch()
    {
        var api = new TrackingScenario(factory.CreateClient());
        var (_, _, companyToken) = await api.SignupAdminAsync("Retention Co");
        var (staffId, _) = await api.SignupStaffAsync(companyToken, "Ancient");

        var stale = DateTimeOffset.UtcNow.AddDays(-40);
        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            var companyId = await db.Users.Where(u => u.Id == staffId).Select(u => u.CompanyId).SingleAsync();

            // Past one batch, so a pruner that stops after the first pass leaves rows behind — the
            // failure mode that let agent_events grow without bound at any retention setting.
            db.AgentEvents.AddRange(Enumerable.Range(0, 5100).Select(i => new AgentEvent
            {
                Id = Guid.NewGuid(),
                CompanyId = companyId,
                UserId = staffId,
                ClientEventId = Guid.NewGuid(),
                EventType = AgentEventTypes.Heartbeat,
                OccurredAt = stale.AddSeconds(i),
                ReceivedAt = stale.AddSeconds(i),
                PayloadJson = "{}"
            }));
            await db.SaveChangesAsync();
        }

        var retention = new RetentionHostedService(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new RetentionOptions { AgentEventsDays = 30 }),
            NullLogger<RetentionHostedService>.Instance);
        await retention.RunOnceAsync(CancellationToken.None);

        using var after = factory.Services.CreateScope();
        var verify = after.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await verify.AgentEvents.CountAsync(e => e.UserId == staffId && e.OccurredAt < stale.AddDays(1)));
    }

    private static async Task<HttpStatusCode> SignupAsync(HttpClient client, string deviceKey, string username)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(deviceKey), "deviceKey" },
            { new StringContent(username), "username" },
            { new StringContent("password123"), "password" }
        };
        var resp = await client.PostAsync("/api/auth/admin/signup", form);
        Assert.True(
            resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest,
            $"signup -> {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        return resp.StatusCode;
    }
}
