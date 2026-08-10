using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Tests;

/// <summary>
/// Every case here used to sign up an ADMIN and ingest with the admin's token, so the whole ingest
/// contract was proved against the one caller §4.5 forbids — and the guard that now rejects it had
/// to be written before anything noticed. They ingest as staff, which is the only caller that
/// exists in production.
/// </summary>
public class IngestFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public IngestFlowTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(_ => { });
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task IngestBatch_Accepts_And_Dedupes()
    {
        var (_, companyToken) = await SignupAdminAsync("IngestCo");
        var staffToken = await SignupStaffAsync(companyToken);

        var eventId = Guid.NewGuid();
        var batch = Batch(new IngestEventDto
        {
            ClientEventId = eventId,
            EventType = AgentEventTypes.Connectivity,
            OccurredAt = DateTimeOffset.UtcNow,
            PayloadJson = """{"isOnline":true,"apiReachable":true,"latencyMs":12}"""
        });

        var body1 = await IngestAsync(staffToken, batch);
        Assert.Contains(eventId.ToString(), body1);

        var body2 = await IngestAsync(staffToken, batch);
        using var r2 = JsonDocument.Parse(body2);
        Assert.Equal(1, r2.RootElement.GetProperty("duplicateIds").GetArrayLength());
    }

    [Fact]
    public async Task IngestBatch_Accepts_Events_With_BusinessLocal_Payload()
    {
        var (_, companyToken) = await SignupAdminAsync("BizLocalCo");
        var staffToken = await SignupStaffAsync(companyToken);

        var batch = Batch(new IngestEventDto
        {
            ClientEventId = Guid.NewGuid(),
            EventType = AgentEventTypes.TimeTrack,
            OccurredAt = DateTimeOffset.UtcNow,
            PayloadJson =
                """{"state":"Working","businessLocal":"2026-08-06T15:01:43","businessTimeZoneId":"Europe/Berlin","businessClockVersion":1,"vaultSequence":1}"""
        });

        using var r = JsonDocument.Parse(await IngestAsync(staffToken, batch));
        Assert.Equal(1, r.RootElement.GetProperty("acceptedIds").GetArrayLength());
    }

    [Fact]
    public async Task IngestBatch_Skips_Poison_Items_Without_Failing_Batch()
    {
        var (_, companyToken) = await SignupAdminAsync("PoisonCo");
        var staffToken = await SignupStaffAsync(companyToken);

        var goodId = Guid.NewGuid();
        var badId = Guid.NewGuid();
        var batch = Batch(
            new IngestEventDto
            {
                ClientEventId = badId,
                EventType = "not_a_real_type",
                OccurredAt = DateTimeOffset.UtcNow,
                PayloadJson = "{}"
            },
            new IngestEventDto
            {
                ClientEventId = goodId,
                EventType = AgentEventTypes.Heartbeat,
                OccurredAt = DateTimeOffset.UtcNow,
                PayloadJson = "{}"
            });

        using var r = JsonDocument.Parse(await IngestAsync(staffToken, batch));
        Assert.Equal(1, r.RootElement.GetProperty("acceptedIds").GetArrayLength());
        Assert.Equal(1, r.RootElement.GetProperty("rejected").GetArrayLength());
        Assert.Equal(
            badId.ToString(),
            r.RootElement.GetProperty("rejected")[0].GetProperty("clientEventId").GetGuid().ToString());
    }

    private static IngestBatchRequest Batch(params IngestEventDto[] events)
        => new() { Events = events };

    private async Task<string> IngestAsync(string token, IngestBatchRequest batch)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/batch")
        {
            Content = JsonContent.Create(batch)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, body);
        return body;
    }

    private async Task<(string AccessToken, string CompanyToken)> SignupAdminAsync(string name)
    {
        var device = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device), "deviceKey" },
            { new StringContent(name + Guid.NewGuid().ToString("N")[..6]), "username" },
            { new StringContent("password123"), "password" }
        };
        var resp = await _client.PostAsync("/api/auth/admin/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("accessToken").GetString()!,
            doc.RootElement.GetProperty("companyToken").GetString()!);
    }

    private async Task<string> SignupStaffAsync(string companyToken)
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
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }

    /// <summary>
    /// The real agent path: SyncApiClient gzips its batches on the wire and the server's request
    /// decompression unwraps them. Proven against the actual pipeline, not a fake — this is the
    /// product's heaviest request, and a broken roundtrip here would silence every agent at once.
    /// </summary>
    [Fact]
    public async Task GzippedBatch_FromTheRealClient_IsAcceptedAndDeduped()
    {
        var (_, companyToken) = await SignupAdminAsync("GzipCo");
        var staffToken = await SignupStaffAsync(companyToken);

        // A fresh client: ApiClientBase sets BaseAddress, which HttpClient forbids after first use.
        using var wire = _factory.CreateClient();
        var api = new SyncApiClient(wire.BaseAddress!.ToString(), wire);
        var item = OutboxItem.Create(AgentEventTypes.Connectivity,
            new { isOnline = true, apiReachable = true, latencyMs = 5 });

        var first = await api.PushBatchAsync(staffToken, [item]);
        Assert.Contains(item.ClientEventId, first.AcceptedIds);

        var again = await api.PushBatchAsync(staffToken, [item]);
        Assert.Contains(item.ClientEventId, again.DuplicateIds);
    }
}
