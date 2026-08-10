using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.Api.Tests;

/// <summary>
/// §6 / §1.4 / §1.5 — the Codes screen in the derived model. There is no enrolment and no stored
/// secret: a code is derived from the machine's device key on company-local time and is always
/// available. The screen is staff only, and the old provisioning surface is gone.
/// </summary>
public sealed class CodesDerivedFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public CodesDerivedFlowTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(_ => { });
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task FromAFreshCompany_TheCodeIsImmediatelyAvailable_AndMatchesTheDeviceKeyDerivation()
    {
        var (adminToken, companyToken, _) = await SignupAdminAsync();
        var (staffId, _, staffDevice) = await SignupStaffAsync(companyToken);

        // §1.5 — staff only. The admin's own machine is not listed, and every staff row is "enabled"
        // (there is no not-enrolled state).
        var roster = await ListStaffAsync(adminToken);
        var row = Assert.Single(roster, r => r.GetProperty("staffUserId").GetGuid() == staffId);
        Assert.True(row.GetProperty("enabled").GetBoolean());
        Assert.Equal("staff", row.GetProperty("role").GetString());

        // §1.4 — no enrolment step: asking for a code first thing returns one, not a 400.
        var usb = await GetCodeAsync(adminToken, staffId, "usb");
        var uninstall = await GetCodeAsync(adminToken, staffId, "uninstall");
        Assert.Equal(6, usb.Length);
        Assert.All(usb, c => Assert.True(char.IsAsciiDigit(c)));
        Assert.NotEqual(usb, uninstall);

        // §6.4 — the exact code the agent would compute offline from the same device key (UTC clock).
        Assert.Equal(TotpGenerator.ComputeCode(TotpGenerator.DeriveMachineSecret(staffDevice, TotpGenerator.PurposeUsb)), usb);
    }

    [Fact]
    public async Task TheProvisioningSurfaceIsGone_EnrollAndSecretsReturn404()
    {
        var (adminToken, companyToken, _) = await SignupAdminAsync();
        var (staffId, staffToken, _) = await SignupStaffAsync(companyToken);

        using var enroll = Authed(HttpMethod.Post, "/api/lifecycle/totp/enroll", adminToken);
        enroll.Content = JsonContent.Create(new { staffUserId = staffId });
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(enroll)).StatusCode);

        using var secrets = Authed(HttpMethod.Get, "/api/lifecycle/totp/me/secrets", staffToken);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(secrets)).StatusCode);
    }

    [Fact]
    public async Task PlainStaff_CannotIssueCodes()
    {
        var (_, companyToken, _) = await SignupAdminAsync();
        var (targetId, _, _) = await SignupStaffAsync(companyToken);
        var (_, plainToken, _) = await SignupStaffAsync(companyToken);

        using var req = Authed(HttpMethod.Get, $"/api/lifecycle/totp/code/{targetId:D}", plainToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(req)).StatusCode);
    }

    private async Task<List<JsonElement>> ListStaffAsync(string token)
    {
        using var req = Authed(HttpMethod.Get, "/api/lifecycle/totp/staff", token);
        var resp = await _client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private async Task<string> GetCodeAsync(string token, Guid staffId, string purpose)
    {
        using var req = Authed(HttpMethod.Get, $"/api/lifecycle/totp/code/{staffId:D}?purpose={purpose}", token);
        var resp = await _client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("code").GetString()!;
    }

    private async Task<(string AccessToken, string CompanyToken, string DeviceKey)> SignupAdminAsync()
    {
        var device = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device), "deviceKey" },
            { new StringContent("Codes " + Guid.NewGuid().ToString("N")[..6]), "username" },
            { new StringContent("password123"), "password" }
        };
        var resp = await _client.PostAsync("/api/auth/admin/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("accessToken").GetString()!,
            doc.RootElement.GetProperty("companyToken").GetString()!,
            device);
    }

    private async Task<(Guid Id, string Token, string DeviceKey)> SignupStaffAsync(string companyToken)
    {
        var device = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device), "deviceKey" },
            { new StringContent("Staff" + Guid.NewGuid().ToString("N")[..6]), "username" },
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
