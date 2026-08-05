using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Teamscop.Engine.Auth;

namespace Teamscop.Api.Tests;

public class AuthFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public AuthFlowTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(_ => { });
    }

    [Fact]
    public async Task AdminSignup_StaffSignup_Login_Me_Reveal_Works()
    {
        var client = _factory.CreateClient();
        var adminDevice = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
        adminDevice = adminDevice.ToLowerInvariant();

        using var adminForm = new MultipartFormDataContent
        {
            { new StringContent(adminDevice), "deviceKey" },
            { new StringContent("Acme Business"), "username" },
            { new StringContent("password123"), "password" }
        };

        var adminResp = await client.PostAsync("/api/auth/admin/signup", adminForm);
        var adminBody = await adminResp.Content.ReadAsStringAsync();
        Assert.True(adminResp.IsSuccessStatusCode, adminBody);
        var adminSession = JsonSerializer.Deserialize<AuthSession>(adminBody, JsonOptions);
        Assert.NotNull(adminSession);
        Assert.False(string.IsNullOrWhiteSpace(adminSession!.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(adminSession.CompanyToken));
        Assert.Equal("admin", adminSession.User.Role);

        var codec = CompanyTokenCodec.FromBase64Key("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
        Assert.True(codec.TryDecrypt(adminSession.CompanyToken!, out var peek));
        Assert.Equal("Acme Business", peek!.CompanyName);

        var staffDevice = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
        staffDevice = staffDevice.ToLowerInvariant();

        using var staffForm = new MultipartFormDataContent
        {
            { new StringContent(staffDevice), "deviceKey" },
            { new StringContent("Staff One"), "username" },
            { new StringContent("password123"), "password" },
            { new StringContent(adminSession.CompanyToken!), "companyToken" }
        };

        var staffResp = await client.PostAsync("/api/auth/staff/signup", staffForm);
        var staffBody = await staffResp.Content.ReadAsStringAsync();
        Assert.True(staffResp.IsSuccessStatusCode, staffBody);
        var staffSession = JsonSerializer.Deserialize<AuthSession>(staffBody, JsonOptions);
        Assert.NotNull(staffSession);
        Assert.Equal("staff", staffSession!.User.Role);
        Assert.Equal(adminSession.User.Company.Id, staffSession.User.Company.Id);

        var loginResp = await client.PostAsJsonAsync("/api/auth/login", new
        {
            deviceKey = staffDevice,
            password = "password123"
        });
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
        var loginSession = await loginResp.Content.ReadFromJsonAsync<AuthSession>(JsonOptions);
        Assert.NotNull(loginSession);

        using var meReq = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        meReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", loginSession!.AccessToken);
        var meResp = await client.SendAsync(meReq);
        Assert.Equal(HttpStatusCode.OK, meResp.StatusCode);
        var me = await meResp.Content.ReadFromJsonAsync<AuthUser>(JsonOptions);
        Assert.Equal("Staff One", me!.Username);

        using var revealReq = new HttpRequestMessage(HttpMethod.Post, "/api/auth/company-token/reveal");
        revealReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminSession.AccessToken);
        var revealResp = await client.SendAsync(revealReq);
        Assert.Equal(HttpStatusCode.OK, revealResp.StatusCode);
        using var revealDoc = JsonDocument.Parse(await revealResp.Content.ReadAsStringAsync());
        Assert.True(revealDoc.RootElement.TryGetProperty("companyToken", out var tokenEl));
        Assert.StartsWith("TS1.", tokenEl.GetString());
    }

    [Fact]
    public async Task StaffSignup_WithoutToken_Fails()
    {
        var client = _factory.CreateClient();
        var device = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device.ToLowerInvariant()), "deviceKey" },
            { new StringContent("Orphan"), "username" },
            { new StringContent("password123"), "password" },
            { new StringContent(""), "companyToken" }
        };

        var resp = await client.PostAsync("/api/auth/staff/signup", form);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
