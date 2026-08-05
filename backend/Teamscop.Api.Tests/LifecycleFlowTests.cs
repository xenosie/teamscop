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
    public async Task AdminEnrollStaffTotp_UsbAndUninstall_ShareSameCode()
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

        var staffDevice = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var staffForm = new MultipartFormDataContent
        {
            { new StringContent(staffDevice), "deviceKey" },
            { new StringContent("Staff"), "username" },
            { new StringContent("password123"), "password" },
            { new StringContent(companyToken), "companyToken" }
        };
        var staffResp = await client.PostAsync("/api/auth/staff/signup", staffForm);
        staffResp.EnsureSuccessStatusCode();
        using var staffDoc = JsonDocument.Parse(await staffResp.Content.ReadAsStringAsync());
        var staffId = staffDoc.RootElement.GetProperty("user").GetProperty("id").GetGuid();

        using var enrollReq = new HttpRequestMessage(HttpMethod.Post, "/api/lifecycle/totp/enroll");
        enrollReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        enrollReq.Content = JsonContent.Create(new { staffUserId = staffId });
        var enrollResp = await client.SendAsync(enrollReq);
        var enrollBody = await enrollResp.Content.ReadAsStringAsync();
        Assert.True(enrollResp.IsSuccessStatusCode, enrollBody);
        using var enrollDoc = JsonDocument.Parse(enrollBody);
        var secret = enrollDoc.RootElement.GetProperty("secret").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(secret));

        using var codeReq = new HttpRequestMessage(HttpMethod.Get, $"/api/lifecycle/totp/code/{staffId}");
        codeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        var codeResp = await client.SendAsync(codeReq);
        codeResp.EnsureSuccessStatusCode();
        using var codeDoc = JsonDocument.Parse(await codeResp.Content.ReadAsStringAsync());
        var adminCode = codeDoc.RootElement.GetProperty("code").GetString()!;
        Assert.Equal(6, adminCode.Length);

        var code = TotpGenerator.ComputeCode(secret);
        Assert.Equal(code, adminCode);

        var uninstallResp = await client.PostAsJsonAsync("/api/lifecycle/uninstall/verify", new
        {
            deviceKey = staffDevice,
            totpCode = code
        });
        var uninstallBody = await uninstallResp.Content.ReadAsStringAsync();
        Assert.True(uninstallResp.IsSuccessStatusCode, uninstallBody);
        using var uninstallDoc = JsonDocument.Parse(uninstallBody);
        var ticket = uninstallDoc.RootElement.GetProperty("uninstallTicket").GetString()!;

        var consumeResp = await client.PostAsJsonAsync("/api/lifecycle/uninstall/consume", new { uninstallTicket = ticket });
        Assert.Equal(HttpStatusCode.OK, consumeResp.StatusCode);

        var usbResp = await client.PostAsJsonAsync("/api/lifecycle/usb/verify", new
        {
            deviceKey = staffDevice,
            totpCode = code,
            deviceInstanceId = "REMOVABLE\\E:"
        });
        var usbBody = await usbResp.Content.ReadAsStringAsync();
        Assert.True(usbResp.IsSuccessStatusCode, usbBody);
        using var usbDoc = JsonDocument.Parse(usbBody);
        var usbTicket = usbDoc.RootElement.GetProperty("usbSessionTicket").GetString()!;
        var usbConsume = await client.PostAsJsonAsync("/api/lifecycle/usb/consume", new { usbSessionTicket = usbTicket });
        Assert.Equal(HttpStatusCode.OK, usbConsume.StatusCode);

        var consumeAgain = await client.PostAsJsonAsync("/api/lifecycle/usb/consume", new { usbSessionTicket = usbTicket });
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
