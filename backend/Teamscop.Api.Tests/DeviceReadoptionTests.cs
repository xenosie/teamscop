using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Teamscop.Api.Tests;

/// <summary>
/// §1.1 / §1.3 — identity is the hardware, so a machine that is REINSTALLED is the same machine.
///
/// Before this, a reinstalled staff PC was unenrollable: the device key is derived from hardware
/// and therefore unchanged, so Join was rejected as "already registered" and the error advised
/// asking an admin to remove the old device — a capability the product does not have and never
/// will (§1.7 rules out deleting users). The machine was stuck between a Join it could not do and
/// a Login whose password the employee might not know.
///
/// Holding the company token is the same proof of belonging that authorised the first Join, so it
/// authorises reclaiming the row. The account id is preserved on purpose: history, team placement
/// and TOTP enrolment survive the reinstall, which is the point of a device-bound identity.
/// </summary>
public class DeviceReadoptionTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public DeviceReadoptionTests(WebApplicationFactory<Program> factory)
        => _client = factory.WithWebHostBuilder(_ => { }).CreateClient();

    [Fact]
    public async Task ReinstalledStaffPc_RejoinsAndKeepsItsIdentityAndHistory()
    {
        var (_, companyToken) = await SignupAdminAsync();
        var device = NewDeviceKey();

        var first = await StaffSignupAsync(companyToken, device, "Solara", "first-password");
        var originalId = first.UserId;

        // The PC is wiped and the agent reinstalled. Same hardware -> same device key. The employee
        // joins again with the company token and a new password, because nobody remembers the old one.
        var second = await StaffSignupAsync(companyToken, device, "Solara", "second-password");

        Assert.Equal(originalId, second.UserId);
        Assert.False(string.IsNullOrWhiteSpace(second.AccessToken));

        // The new password works and the old one does not — this is a real re-enrolment, not a no-op.
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(device, "second-password"));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginStatusAsync(device, "first-password"));
    }

    [Fact]
    public async Task Readoption_IsRecordedSoTheAdminCanTellItApartFromANewMachine()
    {
        var (adminToken, companyToken) = await SignupAdminAsync();
        var device = NewDeviceKey();
        var staff = await StaffSignupAsync(companyToken, device, "Solara", "join-password-1");
        await StaffSignupAsync(companyToken, device, "Solara", "join-password-2");

        using var req = Authed(
            HttpMethod.Get,
            $"/api/tracking/events?staffUserId={staff.UserId:D}&eventType=registration", adminToken);
        var resp = await _client.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var payloads = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("payloadJson").GetString() ?? "")
            .ToList();

        Assert.Equal(2, payloads.Count);
        Assert.Contains(payloads, p => p.Contains("\"readopted\":true"));
        Assert.Contains(payloads, p => p.Contains("\"readopted\":false"));
    }

    [Fact]
    public async Task AStaffPcCannotBeReadoptedIntoADifferentBusiness()
    {
        var (_, tokenA) = await SignupAdminAsync();
        var (_, tokenB) = await SignupAdminAsync();
        var device = NewDeviceKey();

        await StaffSignupAsync(tokenA, device, "Solara", "join-password-1");

        // Holding another company's token must not let a machine walk out of the business it is
        // enrolled in, taking its history with it.
        var resp = await RawStaffSignupAsync(tokenB, device, "Solara", "join-password-1");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("different business", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnAdminPcIsNeverSilentlyTurnedIntoAStaffAccount()
    {
        var (_, companyToken) = await SignupAdminAsync();

        // The admin's own machine. Re-adoption must not demote it and hand the owner's console to
        // a monitoring agent.
        var adminDevice = _lastAdminDevice!;
        _ = companyToken;
        var resp = await RawStaffSignupAsync(companyToken, adminDevice, "Sneaky", "join-password-1");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("Admin", await resp.Content.ReadAsStringAsync());
    }

    // ---- helpers ---------------------------------------------------------------------------

    private sealed record Session(Guid UserId, string AccessToken);

    private static string NewDeviceKey()
        => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    private async Task<HttpStatusCode> LoginStatusAsync(string deviceKey, string password)
    {
        var resp = await _client.PostAsync("/api/auth/login", JsonContent(new { deviceKey, password }));
        return resp.StatusCode;
    }

    private static StringContent JsonContent(object o)
        => new(JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json");

    private async Task<Session> StaffSignupAsync(string companyToken, string device, string name, string password)
    {
        var resp = await RawStaffSignupAsync(companyToken, device, name, password);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return new Session(
            doc.RootElement.GetProperty("user").GetProperty("id").GetGuid(),
            doc.RootElement.GetProperty("accessToken").GetString()!);
    }

    private async Task<HttpResponseMessage> RawStaffSignupAsync(
        string companyToken, string device, string name, string password)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device), "deviceKey" },
            { new StringContent(name), "username" },
            { new StringContent(password), "password" },
            { new StringContent(companyToken), "companyToken" }
        };
        return await _client.PostAsync("/api/auth/staff/signup", form);
    }

    /// <summary>The device key of the admin created by the most recent <see cref="SignupAdminAsync"/>.</summary>
    private string? _lastAdminDevice;

    private async Task<(string AccessToken, string CompanyToken)> SignupAdminAsync()
    {
        var device = NewDeviceKey();
        _lastAdminDevice = device;
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device), "deviceKey" },
            { new StringContent("Co " + Guid.NewGuid().ToString("N")[..6]), "username" },
            { new StringContent(Guid.NewGuid().ToString("N")), "password" }
        };
        var resp = await _client.PostAsync("/api/auth/admin/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("accessToken").GetString()!,
            doc.RootElement.GetProperty("companyToken").GetString()!);
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }
}
