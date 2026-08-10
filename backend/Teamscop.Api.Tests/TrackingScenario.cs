using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Tests;

/// <summary>
/// Signup + ingest plumbing shared by the aggregate, gap and paging tests. The older flow tests
/// each carry their own copy; this exists so the four new suites do not add four more.
/// </summary>
internal sealed class TrackingScenario(HttpClient client)
{
    public async Task<(Guid Id, string AccessToken, string CompanyToken)> SignupAdminAsync(string name)
    {
        using var form = NewForm(name);
        var resp = await client.PostAsync("/api/auth/admin/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("user").GetProperty("id").GetGuid(),
            doc.RootElement.GetProperty("accessToken").GetString()!,
            doc.RootElement.GetProperty("companyToken").GetString()!);
    }

    public async Task<(Guid Id, string AccessToken)> SignupStaffAsync(string companyToken, string name)
    {
        using var form = NewForm(name);
        form.Add(new StringContent(companyToken), "companyToken");
        var resp = await client.PostAsync("/api/auth/staff/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("user").GetProperty("id").GetGuid(),
            doc.RootElement.GetProperty("accessToken").GetString()!);
    }

    public async Task GrantAsync(string adminToken, Guid staffUserId, params string[] packages)
    {
        using var req = Authed(HttpMethod.Put, $"/api/police/{staffUserId:D}", adminToken);
        req.Content = JsonContent.Create(new { packages });
        (await client.SendAsync(req)).EnsureSuccessStatusCode();
    }

    public Task IngestTimeTrackAsync(string token, bool working, DateTimeOffset startedAt, DateTimeOffset endedAt)
        => IngestAsync(token, AgentEventTypes.TimeTrack, endedAt, JsonSerializer.Serialize(new
        {
            state = working ? "Working" : "Rest",
            startedAtUtc = startedAt.UtcDateTime.ToString("o"),
            endedAtUtc = endedAt.UtcDateTime.ToString("o"),
            durationSeconds = (endedAt - startedAt).TotalSeconds
        }));

    public Task IngestHeartbeatAsync(string token, DateTimeOffset occurredAt, bool helperAlive = true, int pending = 0)
        => IngestAsync(token, AgentEventTypes.Heartbeat, occurredAt, JsonSerializer.Serialize(new
        {
            helperAlive,
            trackingOk = true,
            pending
        }));

    public async Task IngestAsync(string token, string eventType, DateTimeOffset occurredAt, string payloadJson)
        => (await IngestForResultAsync(token, eventType, occurredAt, payloadJson)).Dispose();

    /// <summary>Same ingest, but hands back the batch response for the tests that assert on it.</summary>
    public async Task<JsonDocument> IngestForResultAsync(
        string token, string eventType, DateTimeOffset occurredAt, string payloadJson)
    {
        using var req = Authed(HttpMethod.Post, "/api/ingest/batch", token);
        req.Content = JsonContent.Create(new IngestBatchRequest
        {
            Events =
            [
                new IngestEventDto
                {
                    ClientEventId = Guid.NewGuid(),
                    EventType = eventType,
                    OccurredAt = occurredAt,
                    PayloadJson = payloadJson
                }
            ]
        });
        var resp = await client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"POST /api/ingest/batch -> {(int)resp.StatusCode} {text}");
        return JsonDocument.Parse(text);
    }

    /// <summary>Creates a team with a leader and one member, and returns the team id.</summary>
    public async Task<Guid> CreateTeamAsync(string adminToken, string name, Guid leaderId, params Guid[] memberIds)
    {
        using var created = await SendJsonAsync(HttpMethod.Post, "/api/teams", adminToken,
            new { name, leaderUserId = leaderId });
        var teamId = created.RootElement.GetProperty("teamId").GetGuid();
        using var _ = await SendJsonAsync(HttpMethod.Put, $"/api/teams/{teamId:D}/members", adminToken,
            new { memberUserIds = memberIds });
        return teamId;
    }

    public async Task<JsonDocument> SendJsonAsync(HttpMethod method, string url, string token, object body)
    {
        using var req = Authed(method, url, token);
        req.Content = JsonContent.Create(body);
        var resp = await client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"{method} {url} -> {(int)resp.StatusCode} {text}");
        return JsonDocument.Parse(text);
    }

    public async Task<HttpResponseMessage> GetAsync(string url, string token)
    {
        using var req = Authed(HttpMethod.Get, url, token);
        return await client.SendAsync(req);
    }

    public async Task<JsonDocument> GetJsonAsync(string url, string token)
    {
        var resp = await GetAsync(url, token);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"{url} -> {(int)resp.StatusCode} {body}");
        return JsonDocument.Parse(body);
    }

    public static string Iso(DateTimeOffset value)
        => Uri.EscapeDataString(value.UtcDateTime.ToString("o"));

    private static HttpRequestMessage Authed(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static MultipartFormDataContent NewForm(string username) => new()
    {
        { new StringContent(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")), "deviceKey" },
        { new StringContent(username), "username" },
        { new StringContent("password123"), "password" }
    };
}
