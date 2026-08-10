using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Teamscop.Api.Services;

namespace Teamscop.Api.Tests;

public class BusinessTimeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public BusinessTimeTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(_ => { }).CreateClient();
    }

    [Fact]
    public void Resolve_AcceptsIanaAndFixedOffset_RejectsGarbage()
    {
        Assert.True(CompanyBusinessTime.TryResolve("Europe/Berlin", out _));
        Assert.True(CompanyBusinessTime.TryResolve("UTC+03:00", out var fixedZone));
        Assert.Equal(TimeSpan.FromHours(3), fixedZone.GetUtcOffset(DateTime.UtcNow));

        // L4: an unknown id must NOT silently become UTC.
        Assert.False(CompanyBusinessTime.TryResolve("Middle/Earth", out _));
        Assert.False(CompanyBusinessTime.TryResolve("", out _));
    }

    [Fact]
    public void TryParseCompanyDay_AcceptsABareDay_RejectsAFullTimestamp()
    {
        Assert.True(CompanyBusinessTime.TryParseCompanyDay("2026-08-06", out var day));
        Assert.Equal(new DateOnly(2026, 8, 6), day);
        // A full instant is absolute, not a calendar day — it must pass through unconverted.
        Assert.False(CompanyBusinessTime.TryParseCompanyDay("2026-08-06T00:00:00Z", out _));
        Assert.False(CompanyBusinessTime.TryParseCompanyDay("", out _));
        Assert.False(CompanyBusinessTime.TryParseCompanyDay("nonsense", out _));
    }

    [Fact]
    public void PeriodBounds_InclusiveDayRange_MapsToHalfOpenInstants_WithNoGapAtMidnight()
    {
        Assert.True(CompanyBusinessTime.TryResolve("UTC+03:00", out var zone));

        // A single company-local day is a contiguous 24h in UTC.
        var d = new DateOnly(2026, 6, 15);
        var (from, to) = CompanyBusinessTime.PeriodBounds(d, d, zone);
        Assert.Equal(new DateTimeOffset(2026, 6, 14, 21, 0, 0, TimeSpan.Zero), from);
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 21, 0, 0, TimeSpan.Zero), to);
        Assert.Equal(TimeSpan.FromHours(24), to - from);

        // The upper edge of day D is exactly the lower edge of day D+1 — no gap, no overlap, so a
        // row at company-local D+1 00:00 belongs to D+1, never double-counted (half-open [from, to)).
        Assert.Equal(CompanyBusinessTime.DayBounds(d, zone).To, CompanyBusinessTime.DayBounds(d.AddDays(1), zone).From);

        // An inclusive multi-day selection [D .. D+2] spans exactly three days.
        var (mFrom, mTo) = CompanyBusinessTime.PeriodBounds(d, d.AddDays(2), zone);
        Assert.Equal(from, mFrom);
        Assert.Equal(TimeSpan.FromDays(3), mTo - mFrom);
    }

    [Fact]
    public void PeriodBounds_AcrossSpringForwardAndFallBack_YieldA23hAnd25hDay()
    {
        Assert.True(CompanyBusinessTime.TryResolve("Europe/Berlin", out var berlin));

        // Last Sunday of March 2026: clocks jump 02:00 -> 03:00, so the day is 23 hours long.
        var spring = new DateOnly(2026, 3, 29);
        var (sf, st) = CompanyBusinessTime.PeriodBounds(spring, spring, berlin);
        Assert.Equal(TimeSpan.FromHours(23), st - sf);

        // Last Sunday of October 2026: clocks fall 03:00 -> 02:00, so the day is 25 hours long.
        var fall = new DateOnly(2026, 10, 25);
        var (ff, ft) = CompanyBusinessTime.PeriodBounds(fall, fall, berlin);
        Assert.Equal(TimeSpan.FromHours(25), ft - ff);
    }

    [Fact]
    public void PeriodBounds_WhenMidnightItselfIsSkippedByDst_DoesNotThrow_AndTheDayIsStillValid()
    {
        // The bug the guarded ToInstant defends against: in some IANA zones the clocks spring
        // forward AT 00:00, so midnight does not exist that day. An unguarded wall→instant throws;
        // PeriodBounds must advance to the next valid instant instead of crashing the calendar.
        string[] candidates =
            ["America/Santiago", "America/Havana", "America/Asuncion", "America/Sao_Paulo"];

        foreach (var id in candidates)
        {
            if (!CompanyBusinessTime.TryResolve(id, out var zone))
            {
                continue;
            }

            for (var d = new DateOnly(2015, 1, 1); d < new DateOnly(2020, 1, 1); d = d.AddDays(1))
            {
                if (!zone.IsInvalidTime(d.ToDateTime(TimeOnly.MinValue)))
                {
                    continue;
                }

                // Found a real day whose midnight is skipped. This must not throw, and must yield a
                // sane, ordered, ~23h day.
                var (from, to) = CompanyBusinessTime.PeriodBounds(d, d, zone);
                Assert.True(to > from);
                Assert.Equal(TimeSpan.FromHours(23), to - from);
                return;
            }
        }

        Assert.Fail("No midnight-spring-forward day found in any candidate zone — tz data may be missing.");
    }

    [Fact]
    public void ToBusinessLocal_IsAWallClock_NotAnInstant()
    {
        var utc = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);
        Assert.True(CompanyBusinessTime.TryResolve("UTC+03:00", out var zone));

        var local = CompanyBusinessTime.ToBusinessLocal(utc, zone);
        Assert.Equal(new DateTime(2026, 8, 5, 13, 0, 0), local);
        Assert.Equal(DateTimeKind.Unspecified, local.Kind);
        Assert.Equal("2026-08-05T13:00:00", CompanyBusinessTime.ToBusinessLocalIso(utc, zone));
    }

    [Fact]
    public async Task AdminSetsTimeZone_IsVisibleToStaff_ImmediatelyViaApi()
    {
        var adminDevice = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")).ToLowerInvariant();
        using var adminForm = new MultipartFormDataContent
        {
            { new StringContent(adminDevice), "deviceKey" },
            { new StringContent("BizTime Co"), "username" },
            { new StringContent("password123"), "password" }
        };
        var adminResp = await _client.PostAsync("/api/auth/admin/signup", adminForm);
        adminResp.EnsureSuccessStatusCode();
        using var adminDoc = JsonDocument.Parse(await adminResp.Content.ReadAsStringAsync());
        var adminToken = adminDoc.RootElement.GetProperty("accessToken").GetString()!;
        var companyToken = adminDoc.RootElement.GetProperty("companyToken").GetString()!;

        var staffDevice = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")).ToLowerInvariant();
        using var staffForm = new MultipartFormDataContent
        {
            { new StringContent(staffDevice), "deviceKey" },
            { new StringContent("Staff Biz"), "username" },
            { new StringContent("password123"), "password" },
            { new StringContent(companyToken), "companyToken" }
        };
        var staffResp = await _client.PostAsync("/api/auth/staff/signup", staffForm);
        staffResp.EnsureSuccessStatusCode();
        using var staffDoc = JsonDocument.Parse(await staffResp.Content.ReadAsStringAsync());
        var staffToken = staffDoc.RootElement.GetProperty("accessToken").GetString()!;

        using var setReq = new HttpRequestMessage(HttpMethod.Put, "/api/business-time")
        {
            Content = JsonContent.Create(new { timeZoneId = "UTC+03:00" })
        };
        setReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var setResp = await _client.SendAsync(setReq);
        var setBody = await setResp.Content.ReadAsStringAsync();
        Assert.True(setResp.IsSuccessStatusCode, setBody);
        using var setDoc = JsonDocument.Parse(setBody);
        Assert.Equal("UTC+03:00", setDoc.RootElement.GetProperty("timeZoneId").GetString());

        using var staffGet = new HttpRequestMessage(HttpMethod.Get, "/api/business-time/me");
        staffGet.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffToken);
        var staffGetResp = await _client.SendAsync(staffGet);
        Assert.Equal(HttpStatusCode.OK, staffGetResp.StatusCode);
        using var staffCfg = JsonDocument.Parse(await staffGetResp.Content.ReadAsStringAsync());
        Assert.Equal("UTC+03:00", staffCfg.RootElement.GetProperty("timeZoneId").GetString());

        using var nowReq = new HttpRequestMessage(HttpMethod.Get, "/api/business-time/now");
        nowReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffToken);
        var nowResp = await _client.SendAsync(nowReq);
        Assert.Equal(HttpStatusCode.OK, nowResp.StatusCode);
        using var nowDoc = JsonDocument.Parse(await nowResp.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(nowDoc.RootElement.GetProperty("businessLocal").GetString()));
    }

    [Fact]
    public async Task SetTimeZone_UnknownId_Is400_AndDoesNotFallBackToUtc()
    {
        var adminDevice = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")).ToLowerInvariant();
        using var adminForm = new MultipartFormDataContent
        {
            { new StringContent(adminDevice), "deviceKey" },
            { new StringContent("BadZone Co"), "username" },
            { new StringContent("password123"), "password" }
        };
        var adminResp = await _client.PostAsync("/api/auth/admin/signup", adminForm);
        adminResp.EnsureSuccessStatusCode();
        using var adminDoc = JsonDocument.Parse(await adminResp.Content.ReadAsStringAsync());
        var adminToken = adminDoc.RootElement.GetProperty("accessToken").GetString()!;

        using var badReq = new HttpRequestMessage(HttpMethod.Put, "/api/business-time")
        {
            Content = JsonContent.Create(new { timeZoneId = "Middle/Earth" })
        };
        badReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(badReq)).StatusCode);

        using var meReq = new HttpRequestMessage(HttpMethod.Get, "/api/business-time/me");
        meReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var meDoc = JsonDocument.Parse(await (await _client.SendAsync(meReq)).Content.ReadAsStringAsync());
        Assert.Equal("UTC", meDoc.RootElement.GetProperty("timeZoneId").GetString());
    }

    [Fact]
    public async Task SetTimeZone_NonAdmin_Is403()
    {
        var adminDevice = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")).ToLowerInvariant();
        using var adminForm = new MultipartFormDataContent
        {
            { new StringContent(adminDevice), "deviceKey" },
            { new StringContent("ZoneGuard Co"), "username" },
            { new StringContent("password123"), "password" }
        };
        var adminResp = await _client.PostAsync("/api/auth/admin/signup", adminForm);
        adminResp.EnsureSuccessStatusCode();
        using var adminDoc = JsonDocument.Parse(await adminResp.Content.ReadAsStringAsync());
        var companyToken = adminDoc.RootElement.GetProperty("companyToken").GetString()!;

        var staffDevice = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")).ToLowerInvariant();
        using var staffForm = new MultipartFormDataContent
        {
            { new StringContent(staffDevice), "deviceKey" },
            { new StringContent("Zone Staff"), "username" },
            { new StringContent("password123"), "password" },
            { new StringContent(companyToken), "companyToken" }
        };
        var staffResp = await _client.PostAsync("/api/auth/staff/signup", staffForm);
        staffResp.EnsureSuccessStatusCode();
        using var staffDoc = JsonDocument.Parse(await staffResp.Content.ReadAsStringAsync());
        var staffToken = staffDoc.RootElement.GetProperty("accessToken").GetString()!;

        using var req = new HttpRequestMessage(HttpMethod.Put, "/api/business-time")
        {
            Content = JsonContent.Create(new { timeZoneId = "Europe/Berlin" })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(req)).StatusCode);
    }
}
