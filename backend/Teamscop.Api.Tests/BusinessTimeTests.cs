using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Teamscop.Engine.Tracking;

namespace Teamscop.Api.Tests;

public class BusinessTimeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public BusinessTimeTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(_ => { }).CreateClient();
    }

    [Fact]
    public void BusinessClock_Anchor_KeepsStaffInSync()
    {
        var anchorUtc = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);
        var cfg = new BusinessClockConfig
        {
            TimeZoneId = "UTC+03:00",
            IsSynchronized = true,
            ClockVersion = 1,
            AnchorUtc = anchorUtc,
            AnchorYear = 2026,
            AnchorMonth = 8,
            AnchorDay = 5,
            AnchorHour = 13,
            AnchorMinute = 0,
            AnchorSecond = 0
        };

        var a = new BusinessClock();
        a.Apply(cfg);
        var b = new BusinessClock();
        b.Apply(cfg);

        var at = anchorUtc.AddMinutes(45);
        Assert.Equal(a.At(at).BusinessLocalIso, b.At(at).BusinessLocalIso);
        Assert.Equal("2026-08-05T13:45:00", a.At(at).BusinessLocalIso);
    }

    [Fact]
    public async Task AdminDeclare_IsVisibleToStaff_ImmediatelyViaApi()
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

        using var declareReq = new HttpRequestMessage(HttpMethod.Post, "/api/business-time/declare")
        {
            Content = JsonContent.Create(new
            {
                timeZoneId = "UTC+03:00",
                year = 2026,
                month = 8,
                day = 5,
                hour = 14,
                minute = 30,
                second = 0
            })
        };
        declareReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var declareResp = await _client.SendAsync(declareReq);
        var declareBody = await declareResp.Content.ReadAsStringAsync();
        Assert.True(declareResp.IsSuccessStatusCode, declareBody);
        using var declareDoc = JsonDocument.Parse(declareBody);
        Assert.True(declareDoc.RootElement.GetProperty("isSynchronized").GetBoolean());
        Assert.True(declareDoc.RootElement.GetProperty("clockVersion").GetInt64() >= 1);

        using var staffGet = new HttpRequestMessage(HttpMethod.Get, "/api/business-time/me");
        staffGet.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffToken);
        var staffGetResp = await _client.SendAsync(staffGet);
        Assert.Equal(HttpStatusCode.OK, staffGetResp.StatusCode);
        using var staffCfg = JsonDocument.Parse(await staffGetResp.Content.ReadAsStringAsync());
        Assert.Equal("UTC+03:00", staffCfg.RootElement.GetProperty("timeZoneId").GetString());
        Assert.Equal(14, staffCfg.RootElement.GetProperty("anchorHour").GetInt32());

        using var nowReq = new HttpRequestMessage(HttpMethod.Get, "/api/business-time/now");
        nowReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffToken);
        var nowResp = await _client.SendAsync(nowReq);
        Assert.Equal(HttpStatusCode.OK, nowResp.StatusCode);
        using var nowDoc = JsonDocument.Parse(await nowResp.Content.ReadAsStringAsync());
        Assert.True(nowDoc.RootElement.GetProperty("synchronized").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(nowDoc.RootElement.GetProperty("businessLocal").GetString()));
    }
}
