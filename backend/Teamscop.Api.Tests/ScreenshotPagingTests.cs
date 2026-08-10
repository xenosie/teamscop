using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Tests;

/// <summary>
/// §15.1 — a day of captures is hundreds of tiles, so the gallery pages backwards on a cursor.
/// Offset paging drifts as new captures land at the head of the list.
/// </summary>
public class ScreenshotPagingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly TrackingScenario _api;

    public ScreenshotPagingTests(WebApplicationFactory<Program> factory)
        => _api = new TrackingScenario(factory.WithWebHostBuilder(_ => { }).CreateClient());

    [Fact]
    public async Task Screenshots_PageBackwardsOnTheBeforeCursor_WithoutDrift()
    {
        var (_, adminToken, companyToken) = await _api.SignupAdminAsync("Gallery Co");
        var (staffId, staffToken) = await _api.SignupStaffAsync(companyToken, "Captured");

        var newest = DateTimeOffset.UtcNow;
        var times = new[] { newest.AddMinutes(-9), newest.AddMinutes(-6), newest.AddMinutes(-3), newest };
        foreach (var t in times)
        {
            await _api.IngestAsync(staffToken, AgentEventTypes.ScreenshotMeta, t,
                """{"displays":[{"displayIndex":1,"width":800,"height":600,"size":1234}]}""");
        }

        var from = TrackingScenario.Iso(newest.AddHours(-1));
        var to = TrackingScenario.Iso(newest.AddMinutes(1));

        using var first = await _api.GetJsonAsync(
            $"/api/tracking/screenshots?staffUserId={staffId:D}&from={from}&to={to}&take=2", adminToken);
        var page1 = first.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, page1.Count);
        Assert.Equal(times[3].ToUnixTimeSeconds(), Occurred(page1[0]));
        Assert.Equal(times[2].ToUnixTimeSeconds(), Occurred(page1[1]));

        var cursor = TrackingScenario.Iso(page1[1].GetProperty("occurredAt").GetDateTimeOffset());
        using var second = await _api.GetJsonAsync(
            $"/api/tracking/screenshots?staffUserId={staffId:D}&from={from}&to={to}&before={cursor}&take=2",
            adminToken);
        var page2 = second.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, page2.Count);
        Assert.Equal(times[1].ToUnixTimeSeconds(), Occurred(page2[0]));
        Assert.Equal(times[0].ToUnixTimeSeconds(), Occurred(page2[1]));

        // The cursor is exclusive, so the two pages never overlap and never skip.
        var cursor2 = TrackingScenario.Iso(page2[1].GetProperty("occurredAt").GetDateTimeOffset());
        using var third = await _api.GetJsonAsync(
            $"/api/tracking/screenshots?staffUserId={staffId:D}&from={from}&to={to}&before={cursor2}&take=2",
            adminToken);
        Assert.Empty(third.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task Screenshots_BeforeCursor_CannotEscapeTheRequestedPeriod()
    {
        var (_, adminToken, companyToken) = await _api.SignupAdminAsync("Gallery Bounds Co");
        var (staffId, staffToken) = await _api.SignupStaffAsync(companyToken, "Bounded");

        var now = DateTimeOffset.UtcNow;
        await _api.IngestAsync(staffToken, AgentEventTypes.ScreenshotMeta, now.AddDays(-2),
            """{"displays":[{"displayIndex":1,"width":800,"height":600,"size":1}]}""");
        await _api.IngestAsync(staffToken, AgentEventTypes.ScreenshotMeta, now.AddMinutes(-5),
            """{"displays":[{"displayIndex":1,"width":800,"height":600,"size":2}]}""");

        // A cursor later than `to` must not widen the window back over yesterday's captures.
        var url = $"/api/tracking/screenshots?staffUserId={staffId:D}"
                  + $"&from={TrackingScenario.Iso(now.AddHours(-1))}&to={TrackingScenario.Iso(now)}"
                  + $"&before={TrackingScenario.Iso(now.AddDays(1))}&take=50";
        using var doc = await _api.GetJsonAsync(url, adminToken);
        var items = doc.RootElement.EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal(now.AddMinutes(-5).ToUnixTimeSeconds(), Occurred(items[0]));
    }

    private static long Occurred(JsonElement item)
        => item.GetProperty("occurredAt").GetDateTimeOffset().ToUnixTimeSeconds();
}
