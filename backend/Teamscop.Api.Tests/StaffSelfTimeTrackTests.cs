using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Tests;

public class StaffSelfTimeTrackTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public StaffSelfTimeTrackTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(_ => { }).CreateClient();
    }

    [Fact]
    public async Task PlainStaff_CanReadOwnTimeTrackTimeline_WithoutPackage()
    {
        var (_, companyToken) = await SignupAdminAsync("Self TT Co");
        var (staffId, staffToken, _) = await SignupStaffAsync(companyToken, "PlainStaff");
        var (otherId, otherToken, _) = await SignupStaffAsync(companyToken, "Other");

        var end = DateTimeOffset.UtcNow;
        var start = end.AddHours(-2);
        await IngestTimeTrackAsync(staffToken, "Working", start, start.AddHours(1));
        await IngestTimeTrackAsync(staffToken, "Rest", start.AddHours(1), end);

        var from = end.AddHours(-24);
        var to = end.AddMinutes(1);
        using var selfReq = Authed(HttpMethod.Get,
            $"/api/tracking/timetrack?staffUserId={staffId:D}&from={Uri.EscapeDataString(from.UtcDateTime.ToString("o"))}&to={Uri.EscapeDataString(to.UtcDateTime.ToString("o"))}",
            staffToken);
        var selfResp = await _client.SendAsync(selfReq);
        Assert.True(selfResp.IsSuccessStatusCode, await selfResp.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await selfResp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("segments").GetArrayLength() >= 1);

        // Cannot read another staff's timeline without a package.
        using var otherReq = Authed(HttpMethod.Get,
            $"/api/tracking/timetrack?staffUserId={otherId:D}&from={Uri.EscapeDataString(from.UtcDateTime.ToString("o"))}&to={Uri.EscapeDataString(to.UtcDateTime.ToString("o"))}",
            staffToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(otherReq)).StatusCode);

        // Other staff cannot read this staff either.
        using var peerReq = Authed(HttpMethod.Get,
            $"/api/tracking/timetrack?staffUserId={staffId:D}&from={Uri.EscapeDataString(from.UtcDateTime.ToString("o"))}&to={Uri.EscapeDataString(to.UtcDateTime.ToString("o"))}",
            otherToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(peerReq)).StatusCode);
    }

    private async Task IngestTimeTrackAsync(
        string token, string state, DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        var payload = JsonSerializer.Serialize(new
        {
            state,
            startedAtUtc = startedAt.UtcDateTime.ToString("o"),
            endedAtUtc = endedAt.UtcDateTime.ToString("o"),
            durationSeconds = (endedAt - startedAt).TotalSeconds
        });
        using var ingest = Authed(HttpMethod.Post, "/api/ingest/batch", token);
        ingest.Content = JsonContent.Create(new IngestBatchRequest
        {
            Events =
            [
                new IngestEventDto
                {
                    ClientEventId = Guid.NewGuid(),
                    EventType = AgentEventTypes.TimeTrack,
                    OccurredAt = endedAt,
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
