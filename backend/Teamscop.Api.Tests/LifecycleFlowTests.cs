using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.Api.Tests;

public class LifecycleFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public LifecycleFlowTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(_ => { });
    }

    [Fact]
    public async Task AdminEnrollTotp_StaffUninstallVerify_Works()
    {
        var client = _factory.CreateClient();
        var adminDevice = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var adminForm = new MultipartFormDataContent
        {
            { new StringContent(adminDevice), "deviceKey" },
            { new StringContent("Totp Co"), "username" },
            { new StringContent("password123"), "password" }
        };
        var adminResp = await client.PostAsync("/api/auth/admin/signup", adminForm);
        adminResp.EnsureSuccessStatusCode();
        using var adminDoc = JsonDocument.Parse(await adminResp.Content.ReadAsStringAsync());
        var access = adminDoc.RootElement.GetProperty("accessToken").GetString()!;
        var companyToken = adminDoc.RootElement.GetProperty("companyToken").GetString()!;

        using var enrollReq = new HttpRequestMessage(HttpMethod.Post, "/api/lifecycle/totp/enroll");
        enrollReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        var enrollResp = await client.SendAsync(enrollReq);
        var enrollBody = await enrollResp.Content.ReadAsStringAsync();
        Assert.True(enrollResp.IsSuccessStatusCode, enrollBody);
        using var enrollDoc = JsonDocument.Parse(enrollBody);
        var secret = enrollDoc.RootElement.GetProperty("secret").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(secret));

        var staffDevice = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var staffForm = new MultipartFormDataContent
        {
            { new StringContent(staffDevice), "deviceKey" },
            { new StringContent("Staff"), "username" },
            { new StringContent("password123"), "password" },
            { new StringContent(companyToken), "companyToken" }
        };
        (await client.PostAsync("/api/auth/staff/signup", staffForm)).EnsureSuccessStatusCode();

        var code = TotpGenerator.ComputeCode(secret);
        var verifyResp = await client.PostAsJsonAsync("/api/lifecycle/uninstall/verify", new
        {
            deviceKey = staffDevice,
            totpCode = code
        });
        var verifyBody = await verifyResp.Content.ReadAsStringAsync();
        Assert.True(verifyResp.IsSuccessStatusCode, verifyBody);
        using var verifyDoc = JsonDocument.Parse(verifyBody);
        var ticket = verifyDoc.RootElement.GetProperty("uninstallTicket").GetString()!;

        var consumeResp = await client.PostAsJsonAsync("/api/lifecycle/uninstall/consume", new { uninstallTicket = ticket });
        Assert.Equal(HttpStatusCode.OK, consumeResp.StatusCode);

        var consumeAgain = await client.PostAsJsonAsync("/api/lifecycle/uninstall/consume", new { uninstallTicket = ticket });
        Assert.Equal(HttpStatusCode.Unauthorized, consumeAgain.StatusCode);
    }

    [Fact]
    public void RolePolicy_Staff_DisallowsClose()
    {
        var staff = RolePolicy.For(AgentRole.Staff);
        Assert.False(staff.AllowUserClose);
        Assert.True(staff.RunsAsWindowsService);
        Assert.True(staff.RequireTotpForUninstall);

        var admin = RolePolicy.For(AgentRole.Admin);
        Assert.True(admin.AllowUserClose);
        Assert.False(admin.RunsAsWindowsService);
    }
}
