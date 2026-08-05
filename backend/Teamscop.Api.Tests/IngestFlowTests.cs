using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Tests;

public class IngestFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public IngestFlowTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(_ => { });
    }

    [Fact]
    public async Task IngestBatch_Accepts_And_Dedupes()
    {
        var client = _factory.CreateClient();
        var device = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device), "deviceKey" },
            { new StringContent("IngestCo"), "username" },
            { new StringContent("password123"), "password" }
        };
        var signup = await client.PostAsync("/api/auth/admin/signup", form);
        signup.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await signup.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("accessToken").GetString()!;

        var eventId = Guid.NewGuid();
        var batch = new IngestBatchRequest
        {
            Events =
            [
                new IngestEventDto
                {
                    ClientEventId = eventId,
                    EventType = AgentEventTypes.Connectivity,
                    OccurredAt = DateTimeOffset.UtcNow,
                    PayloadJson = """{"isOnline":true,"apiReachable":true,"latencyMs":12}"""
                }
            ]
        };

        using var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/batch")
        {
            Content = JsonContent.Create(batch)
        };
        req1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp1 = await client.SendAsync(req1);
        var body1 = await resp1.Content.ReadAsStringAsync();
        Assert.True(resp1.IsSuccessStatusCode, body1);
        using var r1 = JsonDocument.Parse(body1);
        Assert.Contains(eventId.ToString(), body1);

        using var req2 = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/batch")
        {
            Content = JsonContent.Create(batch)
        };
        req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp2 = await client.SendAsync(req2);
        var body2 = await resp2.Content.ReadAsStringAsync();
        Assert.True(resp2.IsSuccessStatusCode, body2);
        using var r2 = JsonDocument.Parse(body2);
        Assert.Equal(1, r2.RootElement.GetProperty("duplicateIds").GetArrayLength());
    }

    [Fact]
    public async Task IngestBatch_Skips_Poison_Items_Without_Failing_Batch()
    {
        var client = _factory.CreateClient();
        var device = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device), "deviceKey" },
            { new StringContent("PoisonCo"), "username" },
            { new StringContent("password123"), "password" }
        };
        var signup = await client.PostAsync("/api/auth/admin/signup", form);
        signup.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await signup.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("accessToken").GetString()!;

        var goodId = Guid.NewGuid();
        var badId = Guid.NewGuid();
        var batch = new IngestBatchRequest
        {
            Events =
            [
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
                }
            ]
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/batch")
        {
            Content = JsonContent.Create(batch)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, body);
        using var r = JsonDocument.Parse(body);
        Assert.Equal(1, r.RootElement.GetProperty("acceptedIds").GetArrayLength());
        Assert.Equal(1, r.RootElement.GetProperty("rejected").GetArrayLength());
        Assert.Equal(badId.ToString(), r.RootElement.GetProperty("rejected")[0].GetProperty("clientEventId").GetGuid().ToString());
    }
}
