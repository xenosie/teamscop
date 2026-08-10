using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Teamscop.Api.Data;
using Teamscop.Api.Services;
using Teamscop.Api.Services.Export;

namespace Teamscop.Api.Tests;

/// <summary>
/// §ExportAPI — end-to-end over the real pipeline: real credentials, real Argon2 verification, real
/// allowlist enforcement, real company scoping.
///
/// The tests that matter most are the refusals. This API hands one external service a read channel
/// into every employee's screen, so the interesting question is never "does it return data" but
/// "does it refuse everything it should" — wrong secret, unknown key, no allowlist, wrong IP,
/// another company's staff, another company's screenshot. Each of those has a test below, and each
/// asserts the specific status code the docs promise.
/// </summary>
public class ExportApiFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    /// <summary>
    /// The address every request in this class appears to come from.
    ///
    /// WebApplicationFactory dials in-memory, so Connection.RemoteIpAddress is null — and the API
    /// correctly refuses data to a caller with no address (fail closed). A startup filter stamps a
    /// fixed address so the allowlist can be exercised for real rather than bypassed.
    /// </summary>
    private const string CallerIp = "203.0.113.42";

    public ExportApiFlowTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(
                    new RemoteIpStartupFilter(IPAddress.Parse(CallerIp)))));
        _client = _factory.CreateClient();
    }

    private sealed class RemoteIpStartupFilter(IPAddress ip) : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(
            Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next)
            => app =>
            {
                app.Use(next => async ctx =>
                {
                    ctx.Connection.RemoteIpAddress = ip;
                    await next(ctx);
                });
                next(app);
            };
    }

    [Fact]
    public async Task Docs_AreServedWithoutCredentials()
    {
        var resp = await _client.GetAsync("/api/v2/docs-for-llm.txt");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.Contains("TEAMSCOP EXPORT API v2", text);
        Assert.Contains("X-Api-Key", text);
    }

    [Fact]
    public async Task NoCredentials_Is401()
        => Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v2/staff")).StatusCode);

    [Fact]
    public async Task WrongSecret_Is401()
    {
        var (key, _, _, _) = await IssueAsync("ExportWrongSecret");

        var resp = await SendAsync(HttpMethod.Get, "/api/v2/staff", key, "tss-not-the-secret");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task UnknownKey_Is401_AndIndistinguishableFromWrongSecret()
    {
        var (key, secret, _, _) = await IssueAsync("ExportUnknownKey");

        var unknown = await SendAsync(HttpMethod.Get, "/api/v2/staff", "tsk-nosuchkey", secret);
        var wrongSecret = await SendAsync(HttpMethod.Get, "/api/v2/staff", key, "tss-wrong");

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);
        // Same body: a caller must not be able to tell whether a key id exists.
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await wrongSecret.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Fail closed. An unconfigured allowlist means "no data yet", never "allow everyone" — the
    /// single most dangerous default this API could have.
    /// </summary>
    [Fact]
    public async Task ValidCredentialsButNoAllowlist_Is403_OnEveryDataEndpoint()
    {
        var (key, secret, _, _) = await IssueAsync("ExportNoAllowlist");

        foreach (var path in new[]
                 {
                     "/api/v2/staff", "/api/v2/business-time", "/api/v2/team-leaders",
                     "/api/v2/timetrack?staffUserId=" + Guid.NewGuid() + "&from=2026-08-01T00:00:00Z&to=2026-08-02T00:00:00Z"
                 })
        {
            var resp = await SendAsync(HttpMethod.Get, path, key, secret);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
    }

    /// <summary>
    /// The allowlist endpoints must work on credentials alone. If they required a matching IP, a
    /// consumer whose address changed would be permanently locked out of the only endpoint capable
    /// of restoring their access.
    /// </summary>
    [Fact]
    public async Task AllowlistEndpoint_WorksWithoutBeingOnTheAllowlist()
    {
        var (key, secret, _, _) = await IssueAsync("ExportBootstrap");

        var read = await SendAsync(HttpMethod.Get, "/api/v2/ip-allowlist", key, secret);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var write = await SendAsync(
            HttpMethod.Post, "/api/v2/ip-allowlist", key, secret,
            new { ips = new[] { "203.0.113.9" } });
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
    }

    [Fact]
    public async Task AllowlistRejectsGarbageAndOverlongLists()
    {
        var (key, secret, _, _) = await IssueAsync("ExportBadAllowlist");

        var garbage = await SendAsync(
            HttpMethod.Post, "/api/v2/ip-allowlist", key, secret, new { ips = new[] { "not-an-ip" } });
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);

        var empty = await SendAsync(
            HttpMethod.Post, "/api/v2/ip-allowlist", key, secret, new { ips = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var tooMany = await SendAsync(
            HttpMethod.Post, "/api/v2/ip-allowlist", key, secret,
            new { ips = Enumerable.Range(1, 21).Select(i => $"10.0.0.{i}").ToArray() });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    /// <summary>A credential allowlisted elsewhere must not serve data to this caller.</summary>
    [Fact]
    public async Task AllowlistedForAnotherAddress_Is403()
    {
        var (key, secret, _, _) = await IssueAsync("ExportOtherIp");
        await AllowAsync(key, secret, "203.0.113.250");

        var resp = await SendAsync(HttpMethod.Get, "/api/v2/staff", key, secret);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task AllowlistedForTheCallerAddress_ServesData()
    {
        var (key, secret, companyToken, adminToken) = await IssueAsync("ExportHappy");
        await SignupStaffAsync(companyToken, "ExportStaffA");
        await AllowCallerAsync(key, secret);

        var resp = await SendAsync(HttpMethod.Get, "/api/v2/staff", key, secret);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("staff").GetArrayLength());
    }

    [Fact]
    public async Task BusinessTime_CarriesZoneAndBothClocks()
    {
        var (key, secret, _, _) = await IssueAsync("ExportClock");
        await AllowCallerAsync(key, secret);

        var resp = await SendAsync(HttpMethod.Get, "/api/v2/business-time", key, secret);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("timeZoneId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("businessLocalNow").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("utcNow").GetString()));
    }

    [Fact]
    public async Task TeamsAndLeaders_AreExported()
    {
        var (key, secret, companyToken, adminToken) = await IssueAsync("ExportTeams");
        var (leaderId, _) = await SignupStaffAsync(companyToken, "ExportLeader");
        var (memberId, _) = await SignupStaffAsync(companyToken, "ExportMember");
        await CreateTeamAsync(adminToken, leaderId, memberId);
        await AllowCallerAsync(key, secret);

        var leaders = await SendAsync(HttpMethod.Get, "/api/v2/team-leaders", key, secret);
        Assert.Equal(HttpStatusCode.OK, leaders.StatusCode);
        using var leaderDoc = JsonDocument.Parse(await leaders.Content.ReadAsStringAsync());
        var row = leaderDoc.RootElement.GetProperty("leaders").EnumerateArray().Single();
        Assert.Equal(leaderId, row.GetProperty("staffUserId").GetGuid());
        var teamId = row.GetProperty("teamId").GetGuid();

        var team = await SendAsync(HttpMethod.Get, $"/api/v2/teams/{teamId}/staff", key, secret);
        Assert.Equal(HttpStatusCode.OK, team.StatusCode);
        using var teamDoc = JsonDocument.Parse(await team.Content.ReadAsStringAsync());
        Assert.Equal(memberId, teamDoc.RootElement.GetProperty("members").EnumerateArray().Single()
            .GetProperty("staffUserId").GetGuid());
    }

    /// <summary>Tenant isolation: another company's staff id must not resolve, ever.</summary>
    [Fact]
    public async Task AnotherCompanysStaff_IsNotReadable()
    {
        var (key, secret, _, _) = await IssueAsync("ExportTenantA");
        await AllowCallerAsync(key, secret);

        var (_, otherCompanyToken, _) = await SignupAdminAsync("ExportTenantB");
        var (otherStaffId, _) = await SignupStaffAsync(otherCompanyToken, "ExportOutsider");

        var timetrack = await SendAsync(
            HttpMethod.Get,
            $"/api/v2/timetrack?staffUserId={otherStaffId}&from=2026-08-01T00:00:00Z&to=2026-08-02T00:00:00Z",
            key, secret);
        Assert.Equal(HttpStatusCode.NotFound, timetrack.StatusCode);

        var browsing = await SendAsync(
            HttpMethod.Get,
            $"/api/v2/browsing?staffUserId={otherStaffId}&from=2026-08-01T00:00:00Z&to=2026-08-02T00:00:00Z",
            key, secret);
        Assert.Equal(HttpStatusCode.NotFound, browsing.StatusCode);

        // Screenshots return an empty list rather than 404 — a period query for a staff member you
        // cannot see is legitimately "no rows", and 404 would confirm the id exists elsewhere.
        var shots = await SendAsync(
            HttpMethod.Get,
            $"/api/v2/screenshots?staffUserId={otherStaffId}&from=2026-08-01T00:00:00Z&to=2026-08-02T00:00:00Z",
            key, secret);
        Assert.Equal(HttpStatusCode.OK, shots.StatusCode);
        using var doc = JsonDocument.Parse(await shots.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task PeriodIsRequiredAndBounded()
    {
        var (key, secret, companyToken, adminToken) = await IssueAsync("ExportPeriods");
        var (staffId, _) = await SignupStaffAsync(companyToken, "ExportPeriodStaff");
        await AllowCallerAsync(key, secret);

        var missing = await SendAsync(HttpMethod.Get, $"/api/v2/timetrack?staffUserId={staffId}", key, secret);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var backwards = await SendAsync(
            HttpMethod.Get,
            $"/api/v2/timetrack?staffUserId={staffId}&from=2026-08-10T00:00:00Z&to=2026-08-09T00:00:00Z",
            key, secret);
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);

        var tooLong = await SendAsync(
            HttpMethod.Get,
            $"/api/v2/timetrack?staffUserId={staffId}&from=2026-01-01T00:00:00Z&to=2026-08-01T00:00:00Z",
            key, secret);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task TimeTrack_ReturnsClippedTotalsForRealIngestedData()
    {
        var (key, secret, companyToken, adminToken) = await IssueAsync("ExportTimeTrack");
        var (staffId, staffToken) = await SignupStaffAsync(companyToken, "ExportTtStaff");
        await AllowCallerAsync(key, secret);

        var start = new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);
        await IngestTimeTrackAsync(staffToken, start, start.AddMinutes(10), working: true);
        await IngestTimeTrackAsync(staffToken, start.AddMinutes(10), start.AddMinutes(20), working: false);

        var resp = await SendAsync(
            HttpMethod.Get,
            $"/api/v2/timetrack?staffUserId={staffId}&from={Iso(start)}&to={Iso(start.AddHours(1))}",
            key, secret);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(600, doc.RootElement.GetProperty("workedSeconds").GetDouble(), 1);
        Assert.Equal(600, doc.RootElement.GetProperty("idleSeconds").GetDouble(), 1);
        Assert.Equal(2, doc.RootElement.GetProperty("segments").GetArrayLength());
    }

    [Fact]
    public async Task DisabledKey_Is403()
    {
        var (key, secret, _, _) = await IssueAsync("ExportDisabled");
        await AllowCallerAsync(key, secret);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var client = await db.ApiClients.FirstAsync(c => c.KeyId == key);
            client.Enabled = false;
            await db.SaveChangesAsync();
        }

        // The verification cache keys on (key, secret) and expires; disabling is checked before it,
        // so a disabled key stops working immediately rather than after the cache window.
        var resp = await SendAsync(HttpMethod.Get, "/api/v2/staff", key, secret);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    /// <summary>The export credential must not open any of the product's own JWT routes.</summary>
    [Fact]
    public async Task ExportCredentials_DoNotWorkOnProductEndpoints()
    {
        var (key, secret, _, _) = await IssueAsync("ExportNoCrossover");
        await AllowCallerAsync(key, secret);

        foreach (var path in new[] { "/api/tracking/staff", "/api/org/structure", "/api/police/me" })
        {
            var resp = await SendAsync(HttpMethod.Get, path, key, secret);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static string Iso(DateTimeOffset t) => Uri.EscapeDataString(t.ToString("O"));

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? key, string? secret, object? body = null)
    {
        using var req = new HttpRequestMessage(method, path);
        if (key is not null)
        {
            req.Headers.TryAddWithoutValidation("X-Api-Key", key);
        }

        if (secret is not null)
        {
            req.Headers.TryAddWithoutValidation("X-Api-Secret", secret);
        }

        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(req);
    }

    /// <summary>Allowlists this class's simulated caller address.</summary>
    private Task AllowCallerAsync(string key, string secret) => AllowAsync(key, secret, CallerIp);

    private async Task AllowAsync(string key, string secret, string ip)
    {
        var resp = await SendAsync(HttpMethod.Post, "/api/v2/ip-allowlist", key, secret, new { ips = new[] { ip } });
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Creates a company and issues a credential for it exactly as the CLI would.</summary>
    private async Task<(string Key, string Secret, string CompanyToken, string AdminToken)> IssueAsync(string companyName)
    {
        var (adminToken, companyToken, actualName) = await SignupAdminAsync(companyName);
        var (keyId, secret) = ApiCredentialFactory.Generate();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var company = await db.Companies.FirstAsync(c => c.Name == actualName);
        db.ApiClients.Add(new ApiClient
        {
            Id = Guid.NewGuid(),
            CompanyId = company.Id,
            Name = "test",
            KeyId = keyId,
            SecretHash = hasher.Hash(secret),
            Enabled = true
        });
        await db.SaveChangesAsync();
        return (keyId, secret, companyToken, adminToken);
    }

    /// <summary>Signup is multipart (it accepts an avatar), matching the product's real contract.</summary>
    private async Task<(string AdminToken, string CompanyToken, string CompanyName)> SignupAdminAsync(string companyName)
    {
        var unique = companyName + Guid.NewGuid().ToString("N")[..6];
        using var form = new MultipartFormDataContent
        {
            { new StringContent(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")), "deviceKey" },
            { new StringContent(unique), "username" },
            { new StringContent("password123"), "password" }
        };
        var resp = await _client.PostAsync("/api/auth/admin/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (
            doc.RootElement.GetProperty("accessToken").GetString()!,
            doc.RootElement.GetProperty("companyToken").GetString()!,
            unique);
    }

    private async Task<(Guid StaffId, string StaffToken)> SignupStaffAsync(string companyToken, string username)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")), "deviceKey" },
            { new StringContent(username + Guid.NewGuid().ToString("N")[..6]), "username" },
            { new StringContent("password123"), "password" },
            { new StringContent(companyToken), "companyToken" }
        };
        var resp = await _client.PostAsync("/api/auth/staff/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (
            doc.RootElement.GetProperty("user").GetProperty("id").GetGuid(),
            doc.RootElement.GetProperty("accessToken").GetString()!);
    }

    private async Task CreateTeamAsync(string adminToken, Guid leaderId, Guid memberId)
    {
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/teams");
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        create.Content = JsonContent.Create(new { name = "ExportTeam", leaderUserId = leaderId });
        var resp = await _client.SendAsync(create);
        resp.EnsureSuccessStatusCode();
        var teamId = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("teamId").GetGuid();

        using var members = new HttpRequestMessage(HttpMethod.Put, $"/api/teams/{teamId}/members");
        members.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        members.Content = JsonContent.Create(new { memberUserIds = new[] { memberId } });
        (await _client.SendAsync(members)).EnsureSuccessStatusCode();
    }

    private async Task IngestTimeTrackAsync(
        string staffToken, DateTimeOffset start, DateTimeOffset end, bool working)
    {
        // The agent's real shape: "state" is what marks a window worked or idle.
        var payload = JsonSerializer.Serialize(new
        {
            state = working ? "working" : "rest",
            startedAtUtc = start,
            endedAtUtc = end,
            durationSeconds = (end - start).TotalSeconds
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/batch");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffToken);
        req.Content = JsonContent.Create(new
        {
            events = new[]
            {
                new
                {
                    clientEventId = Guid.NewGuid(),
                    eventType = "timetrack",
                    occurredAt = end,
                    payloadJson = payload
                }
            }
        });
        (await _client.SendAsync(req)).EnsureSuccessStatusCode();
    }
}
