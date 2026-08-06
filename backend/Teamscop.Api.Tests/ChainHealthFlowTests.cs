using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Tests;

public class ChainHealthFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ChainHealthFlowTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(_ => { }).CreateClient();
    }

    [Fact]
    public async Task Admin_CanReadChainHealth_SelfForbidden()
    {
        var (adminToken, companyToken) = await SignupAdminAsync("Chain Co");
        var (staffId, staffToken, _) = await SignupStaffAsync(companyToken, "Tracked");

        await IngestAsync(staffToken, AgentEventTypes.Heartbeat, """{"helperAlive":true,"trackingOk":true,"pending":0}""");
        await IngestAsync(staffToken, AgentEventTypes.VaultAlert,
            """{"ok":false,"chainBreak":true,"highestSequenceFound":12,"expectedNextSequence":14,"error":"gap"}""");

        using var ok = Authed(HttpMethod.Get, $"/api/tracking/chain/{staffId}", adminToken);
        var resp = await _client.SendAsync(ok);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("chainBroken").GetBoolean());
        Assert.Equal(12, doc.RootElement.GetProperty("breakAfterSequence").GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("bannerMessage").GetString()));

        using var self = Authed(HttpMethod.Get, $"/api/tracking/chain/{staffId}", staffToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(self)).StatusCode);
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

    private async Task<(Guid Id, string AccessToken, string DeviceKey)> SignupStaffAsync(
        string companyToken, string name)
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
