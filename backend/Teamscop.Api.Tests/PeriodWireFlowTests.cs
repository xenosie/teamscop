using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Teamscop.Api.Services;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Tests;

/// <summary>
/// §2 — the one company-time chain. The admin's calendar sends company-local days; the server
/// converts them to UTC bounds at one place (§2.3), and the timetrack bar spans exactly the
/// selected period (§2.5). These pin that a day on the wire produces the right instants and that a
/// row on the next day's midnight boundary is not double-counted.
/// </summary>
public sealed class PeriodWireFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly TrackingScenario _api;

    public PeriodWireFlowTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(_ => { });
        _api = new TrackingScenario(_factory.CreateClient());
    }

    [Fact]
    public async Task TimetrackBar_SpansExactlyTheSelectedCompanyLocalDay_InUtc()
    {
        var (_, adminToken, companyToken) = await _api.SignupAdminAsync("Chain Co");
        var (staffId, _) = await _api.SignupStaffAsync(companyToken, "Chained");
        await SetTimeZoneAsync(adminToken, "UTC+03:00");

        var zone = CompanyBusinessTime.Resolve("UTC+03:00");
        var day = CompanyBusinessTime.Today(zone).AddDays(-1);
        var (from, to) = CompanyBusinessTime.DayBounds(day, zone);

        using var doc = await _api.GetJsonAsync(
            $"/api/tracking/timetrack?staffUserId={staffId:D}&from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}", adminToken);

        // The bar's domain (From/To echoed by the server) is exactly the selected day in UTC.
        Assert.Equal(from, doc.RootElement.GetProperty("from").GetDateTimeOffset());
        Assert.Equal(to, doc.RootElement.GetProperty("to").GetDateTimeOffset());
        Assert.Equal((to - from).TotalSeconds, doc.RootElement.GetProperty("totalSeconds").GetDouble(), 3);
    }

    [Fact]
    public async Task ACompanyLocalDaySelection_ExcludesTheNextDaysMidnightBoundaryRow()
    {
        var (_, adminToken, companyToken) = await _api.SignupAdminAsync("Boundary Co");
        var (staffId, staffToken) = await _api.SignupStaffAsync(companyToken, "Edge");
        await SetTimeZoneAsync(adminToken, "UTC+03:00");

        var zone = CompanyBusinessTime.Resolve("UTC+03:00");
        var day = CompanyBusinessTime.Today(zone).AddDays(-1);
        var (from, to) = CompanyBusinessTime.DayBounds(day, zone);

        // One capture safely inside the day, one exactly on the closing midnight (belongs to the
        // NEXT day, half-open), and one an hour into the next day.
        await IngestShotAsync(staffToken, from.AddHours(1));
        await IngestShotAsync(staffToken, to);            // next day's 00:00 — excluded
        await IngestShotAsync(staffToken, to.AddHours(1)); // next day — excluded

        using var doc = await _api.GetJsonAsync(
            $"/api/tracking/screenshots?staffUserId={staffId:D}&from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}&take=50",
            adminToken);
        var items = doc.RootElement.EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal(from.AddHours(1).ToUnixTimeSeconds(),
            items[0].GetProperty("occurredAt").GetDateTimeOffset().ToUnixTimeSeconds());
    }

    private Task IngestShotAsync(string staffToken, DateTimeOffset at)
        => _api.IngestAsync(staffToken, AgentEventTypes.ScreenshotMeta, at,
            """{"displays":[{"displayIndex":1,"width":800,"height":600,"size":1}]}""");

    private async Task SetTimeZoneAsync(string adminToken, string timeZoneId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, "/api/business-time")
        {
            Content = JsonContent.Create(new { timeZoneId })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var resp = await _factory.CreateClient().SendAsync(req);
        resp.EnsureSuccessStatusCode();
    }
}
