using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Tests;

public class AppHistoryFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public AppHistoryFlowTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(_ => { }).CreateClient();
    }

    [Fact]
    public async Task StaffSignup_Emits_Registration_Event()
    {
        var (adminToken, companyToken) = await SignupAdminAsync("History Co");
        var (staffId, _, _) = await SignupStaffAsync(companyToken, "HistStaff");

        using var req = Authed(
            HttpMethod.Get,
            $"/api/tracking/events?staffUserId={staffId}&eventType={AgentEventTypes.Registration}",
            adminToken);
        var resp = await _client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, body);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetArrayLength() >= 1);
        Assert.Equal(AgentEventTypes.Registration, doc.RootElement[0].GetProperty("eventType").GetString());
    }

    [Fact]
    public async Task UninstallConsume_Emits_Uninstall_Event()
    {
        var (adminToken, companyToken) = await SignupAdminAsync("Uninstall Hist Co");
        var (staffId, _, staffDevice) = await SignupStaffAsync(companyToken, "UnStaff");

        // §6.1 — no enrolment; the code is derived on demand from the device key. The admin simply
        // asks for the current uninstall code and relays it (§10.2).
        using var codeReq = Authed(
            HttpMethod.Get,
            $"/api/lifecycle/totp/code/{staffId}?purpose=uninstall",
            adminToken);
        var codeResp = await _client.SendAsync(codeReq);
        codeResp.EnsureSuccessStatusCode();
        var code = JsonDocument.Parse(await codeResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("code").GetString()!;

        var verify = await _client.PostAsJsonAsync("/api/lifecycle/uninstall/verify", new
        {
            deviceKey = staffDevice,
            totpCode = code
        });
        verify.EnsureSuccessStatusCode();
        var ticket = JsonDocument.Parse(await verify.Content.ReadAsStringAsync())
            .RootElement.GetProperty("uninstallTicket").GetString()!;

        var consume = await _client.PostAsJsonAsync("/api/lifecycle/uninstall/consume", new { uninstallTicket = ticket });
        consume.EnsureSuccessStatusCode();

        using var eventsReq = Authed(
            HttpMethod.Get,
            $"/api/tracking/events?staffUserId={staffId}&eventType={AgentEventTypes.Uninstall}",
            adminToken);
        var eventsResp = await _client.SendAsync(eventsReq);
        var eventsBody = await eventsResp.Content.ReadAsStringAsync();
        Assert.True(eventsResp.IsSuccessStatusCode, eventsBody);
        using var eventsDoc = JsonDocument.Parse(eventsBody);
        Assert.True(eventsDoc.RootElement.GetArrayLength() >= 1);
        Assert.Equal(AgentEventTypes.Uninstall, eventsDoc.RootElement[0].GetProperty("eventType").GetString());
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

    private async Task<(Guid Id, string AccessToken, string DeviceKey)> SignupStaffAsync(string companyToken, string name)
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
