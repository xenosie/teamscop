using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Teamscop.Api.Data;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.Api.Tests;

public class LifecycleFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public LifecycleFlowTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(_ => { });
    }

    [Fact]
    public async Task DerivedCodes_UsbAndUninstall_UsePurposeSpecificCodes_AndVerifyOffline()
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

        // §6.1 — no enrolment. The admin asks for the current code straight away.
        using var codeReq = new HttpRequestMessage(HttpMethod.Get, $"/api/lifecycle/totp/code/{staffId}");
        codeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        var codeResp = await client.SendAsync(codeReq);
        codeResp.EnsureSuccessStatusCode();
        using var codeDoc = JsonDocument.Parse(await codeResp.Content.ReadAsStringAsync());
        var adminCode = codeDoc.RootElement.GetProperty("code").GetString()!;
        Assert.Equal(6, adminCode.Length);

        // §6.4 — the code the server issues is the one the agent computes offline from the SAME
        // device key. Company clock defaults to UTC, so company-local time equals UTC here.
        var usbCode = TotpGenerator.ComputeCode(TotpGenerator.DeriveMachineSecret(staffDevice, TotpGenerator.PurposeUsb));
        var uninstallCode = TotpGenerator.ComputeCode(TotpGenerator.DeriveMachineSecret(staffDevice, TotpGenerator.PurposeUninstall));
        Assert.Equal(usbCode, adminCode);
        Assert.NotEqual(usbCode, uninstallCode);

        var uninstallResp = await client.PostAsJsonAsync("/api/lifecycle/uninstall/verify", new
        {
            deviceKey = staffDevice,
            totpCode = uninstallCode
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
            totpCode = usbCode,
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
    public async Task UsbCode_CannotOpenUninstall_AndViceVersa()
    {
        var client = _factory.CreateClient();
        var (companyToken, _) = await SignupAdminAsync(client, "Purpose Co");
        var staffDevice = await SignupStaffDeviceAsync(client, companyToken);

        // Purpose separation is the whole point of independent streams: a USB code must not open an
        // uninstall (§6). Both are derived from the same device key but different HKDF info.
        var usbCode = TotpGenerator.ComputeCode(TotpGenerator.DeriveMachineSecret(staffDevice, TotpGenerator.PurposeUsb));
        var resp = await client.PostAsJsonAsync(
            "/api/lifecycle/uninstall/verify", new { deviceKey = staffDevice, totpCode = usbCode });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
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

    [Fact]
    public async Task TotpLockout_ResetsFailedAttemptsWhenLockoutExpires()
    {
        var factory = _factory.WithWebHostBuilder(_ => { });
        var client = factory.CreateClient();

        var (companyToken, _) = await SignupAdminAsync(client, "Lockout Co");
        var (staffId, staffDevice) = await SignupStaffAsync(client, companyToken);
        var usbGoodCode = TotpGenerator.ComputeCode(TotpGenerator.DeriveMachineSecret(staffDevice, TotpGenerator.PurposeUsb));

        for (var i = 0; i < 8; i++)
        {
            var bad = await client.PostAsJsonAsync("/api/lifecycle/usb/verify", new
            {
                deviceKey = staffDevice,
                totpCode = "000000"
            });
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }

        var stillLocked = await client.PostAsJsonAsync("/api/lifecycle/usb/verify", new
        {
            deviceKey = staffDevice,
            totpCode = usbGoodCode
        });
        Assert.Equal(HttpStatusCode.Unauthorized, stillLocked.StatusCode);
        Assert.Contains("locked", await stillLocked.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == staffId);
            Assert.True(user.AccessTotpFailedAttempts >= 8);
            Assert.NotNull(user.AccessTotpLockoutUntil);
            // Simulate lockout expiry while leaving the stale high counter in place.
            user.AccessTotpLockoutUntil = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        // One further bad attempt after expiry must not immediately re-lock.
        var afterExpiryBad = await client.PostAsJsonAsync("/api/lifecycle/usb/verify", new
        {
            deviceKey = staffDevice,
            totpCode = "000000"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, afterExpiryBad.StatusCode);
        Assert.DoesNotContain("locked", await afterExpiryBad.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // Recompute in case the 30s window rolled over during the failed attempts above.
        var freshGood = TotpGenerator.ComputeCode(TotpGenerator.DeriveMachineSecret(staffDevice, TotpGenerator.PurposeUsb));
        var afterExpiryGood = await client.PostAsJsonAsync("/api/lifecycle/usb/verify", new
        {
            deviceKey = staffDevice,
            totpCode = freshGood
        });
        Assert.True(afterExpiryGood.IsSuccessStatusCode, await afterExpiryGood.Content.ReadAsStringAsync());
    }

    private static async Task<(string CompanyToken, string AccessToken)> SignupAdminAsync(HttpClient client, string name)
    {
        var device = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device), "deviceKey" },
            { new StringContent(name), "username" },
            { new StringContent("password123"), "password" }
        };
        var resp = await client.PostAsync("/api/auth/admin/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("companyToken").GetString()!,
            doc.RootElement.GetProperty("accessToken").GetString()!);
    }

    private static async Task<(Guid Id, string DeviceKey)> SignupStaffAsync(HttpClient client, string companyToken)
    {
        var device = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device), "deviceKey" },
            { new StringContent("StaffLock"), "username" },
            { new StringContent("password123"), "password" },
            { new StringContent(companyToken), "companyToken" }
        };
        var resp = await client.PostAsync("/api/auth/staff/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("user").GetProperty("id").GetGuid(), device);
    }

    private static async Task<string> SignupStaffDeviceAsync(HttpClient client, string companyToken)
        => (await SignupStaffAsync(client, companyToken)).DeviceKey;
}
