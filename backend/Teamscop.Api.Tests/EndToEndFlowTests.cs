using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Sync;
using Teamscop.Engine.Tracking;

namespace Teamscop.Api.Tests;

/// <summary>
/// Full product path: Admin signup → company token → Staff signup → TOTP →
/// tracking config → vault/outbox → ingest → uninstall gate.
/// </summary>
public class EndToEndFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public EndToEndFlowTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(_ => { }).CreateClient();
    }

    [Fact]
    public async Task Full_Admin_Staff_Tracking_Sync_Uninstall_Path()
    {
        // 1) Admin signup
        var adminDevice = NewDeviceKey();
        using var adminForm = SignupForm(adminDevice, "E2E Corp", "password123");
        var adminResp = await _client.PostAsync("/api/auth/admin/signup", adminForm);
        var adminBody = await adminResp.Content.ReadAsStringAsync();
        Assert.True(adminResp.IsSuccessStatusCode, adminBody);
        using var adminDoc = JsonDocument.Parse(adminBody);
        var adminToken = adminDoc.RootElement.GetProperty("accessToken").GetString()!;
        var companyToken = adminDoc.RootElement.GetProperty("companyToken").GetString()!;
        Assert.StartsWith("TS1.", companyToken);

        // Offline decrypt (staff engine path)
        var codec = CompanyTokenCodec.FromBase64Key("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
        Assert.True(codec.TryDecrypt(companyToken, out var peek));
        Assert.Equal("E2E Corp", peek!.CompanyName);

        // 2) Staff signup with company token
        var staffDevice = NewDeviceKey();
        using var staffForm = SignupForm(staffDevice, "E2E Staff", "password123", companyToken);
        var staffResp = await _client.PostAsync("/api/auth/staff/signup", staffForm);
        var staffBody = await staffResp.Content.ReadAsStringAsync();
        Assert.True(staffResp.IsSuccessStatusCode, staffBody);
        using var staffDoc = JsonDocument.Parse(staffBody);
        var staffToken = staffDoc.RootElement.GetProperty("accessToken").GetString()!;
        var staffId = staffDoc.RootElement.GetProperty("user").GetProperty("id").GetGuid();
        Assert.Equal("staff", staffDoc.RootElement.GetProperty("user").GetProperty("role").GetString());

        // 3) Admin enrolls per-staff TOTP (USB + uninstall)
        using var enrollReq = Authed(HttpMethod.Post, "/api/lifecycle/totp/enroll", adminToken);
        enrollReq.Content = JsonContent.Create(new { staffUserId = staffId });
        var enrollResp = await _client.SendAsync(enrollReq);
        var enrollBody = await enrollResp.Content.ReadAsStringAsync();
        Assert.True(enrollResp.IsSuccessStatusCode, enrollBody);
        using var enrollDoc = JsonDocument.Parse(enrollBody);
        var secret = enrollDoc.RootElement.GetProperty("secret").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(secret));

        // 4) Admin configures screenshot/time/browser for staff
        var cfgBody = new StaffTrackingConfig
        {
            StaffUserId = staffId,
            ScreenshotQuality = ScreenshotQuality.Low,
            ScreenshotPeriodSeconds = 90,
            TimeTrackEnabled = true,
            BrowserHistoryEnabled = true,
            ScreenshotEnabled = true
        };
        using var cfgReq = Authed(HttpMethod.Put, $"/api/tracking/config/{staffId}", adminToken);
        cfgReq.Content = JsonContent.Create(cfgBody);
        var cfgResp = await _client.SendAsync(cfgReq);
        var cfgText = await cfgResp.Content.ReadAsStringAsync();
        Assert.True(cfgResp.IsSuccessStatusCode, cfgText);
        using var cfgDoc = JsonDocument.Parse(cfgText);
        Assert.Equal(90, cfgDoc.RootElement.GetProperty("screenshotPeriodSeconds").GetInt32());
        Assert.True(cfgDoc.RootElement.GetProperty("configVersion").GetInt64() >= 1);

        // 5) Staff pulls config
        using var meCfgReq = Authed(HttpMethod.Get, "/api/tracking/config/me", staffToken);
        var meCfgResp = await _client.SendAsync(meCfgReq);
        meCfgResp.EnsureSuccessStatusCode();
        var meCfg = await meCfgResp.Content.ReadFromJsonAsync<StaffTrackingConfig>(Json);
        Assert.NotNull(meCfg);
        Assert.Equal(ScreenshotQuality.Low, meCfg!.ScreenshotQuality);
        Assert.Equal(30 * 1024, meCfg.TargetBytes);

        // 6) Local vault + outbox + sync flush into ingest (agent-side path)
        var root = Path.Combine(Path.GetTempPath(), "e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var master = SecureVault.DeriveMasterKey(staffDevice, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
            var vault = new SecureVault(root, master);
            var outbox = new FileOutboxQueue(root);
            var chrome = new ChromeHistoryWatcher(root);
            var tracking = new TrackingCoordinator(vault, outbox, chrome, meCfg);
            await tracking.TickAsync();

            // Force a timetrack-like vault record and outbox item with sequence envelope
            var append = vault.Append(new VaultRecord
            {
                Kind = "timetrack",
                OccurredAt = DateTimeOffset.UtcNow,
                PlainPayload = """{"state":"Working","idleSeconds":5}"""u8.ToArray()
            });
            var item = OutboxItem.Create(AgentEventTypes.TimeTrack, new
            {
                vaultSequence = append.Sequence,
                chainHash = append.ChainHashHex,
                configVersion = meCfg.ConfigVersion,
                payloadBase64 = Convert.ToBase64String("""{"state":"Working"}"""u8.ToArray())
            });
            await outbox.EnqueueAsync(item);

            var integrity = vault.Verify(fullScan: true);
            Assert.True(integrity.Ok, integrity.Error);

            var pending = await outbox.PeekPendingAsync(50);
            Assert.NotEmpty(pending);

            var batch = new IngestBatchRequest
            {
                Events = pending.Select(p => new IngestEventDto
                {
                    ClientEventId = p.ClientEventId,
                    EventType = p.EventType,
                    OccurredAt = p.OccurredAt,
                    PayloadJson = p.PayloadJson
                }).ToList()
            };

            using var ingestReq = Authed(HttpMethod.Post, "/api/ingest/batch", staffToken);
            ingestReq.Content = JsonContent.Create(batch);
            var ingestResp = await _client.SendAsync(ingestReq);
            var ingestBody = await ingestResp.Content.ReadAsStringAsync();
            Assert.True(ingestResp.IsSuccessStatusCode, ingestBody);
            using var ingestDoc = JsonDocument.Parse(ingestBody);
            Assert.True(ingestDoc.RootElement.GetProperty("acceptedIds").GetArrayLength() > 0);

            await outbox.AcknowledgeAsync(
                ingestDoc.RootElement.GetProperty("acceptedIds").EnumerateArray().Select(x => x.GetGuid())
                    .Concat(ingestDoc.RootElement.GetProperty("duplicateIds").EnumerateArray().Select(x => x.GetGuid())));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        // 7) Staff login + /me
        var loginResp = await _client.PostAsJsonAsync("/api/auth/login", new { deviceKey = staffDevice, password = "password123" });
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
        using var loginDoc = JsonDocument.Parse(await loginResp.Content.ReadAsStringAsync());
        var loginToken = loginDoc.RootElement.GetProperty("accessToken").GetString()!;
        using var meReq = Authed(HttpMethod.Get, "/api/auth/me", loginToken);
        var meResp = await _client.SendAsync(meReq);
        Assert.Equal(HttpStatusCode.OK, meResp.StatusCode);

        // 8) Uninstall gate with TOTP
        var code = TotpGenerator.ComputeCode(secret);
        var verifyResp = await _client.PostAsJsonAsync("/api/lifecycle/uninstall/verify", new
        {
            deviceKey = staffDevice,
            totpCode = code
        });
        var verifyBody = await verifyResp.Content.ReadAsStringAsync();
        Assert.True(verifyResp.IsSuccessStatusCode, verifyBody);
        using var verifyDoc = JsonDocument.Parse(verifyBody);
        var ticket = verifyDoc.RootElement.GetProperty("uninstallTicket").GetString()!;
        var consumeResp = await _client.PostAsJsonAsync("/api/lifecycle/uninstall/consume", new { uninstallTicket = ticket });
        Assert.Equal(HttpStatusCode.OK, consumeResp.StatusCode);

        // Wrong TOTP rejected
        var bad = await _client.PostAsJsonAsync("/api/lifecycle/uninstall/verify", new
        {
            deviceKey = staffDevice,
            totpCode = "000000"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        // Health
        var health = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    private static string NewDeviceKey()
        => (Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray())).ToLowerInvariant();

    private static MultipartFormDataContent SignupForm(string deviceKey, string username, string password, string? companyToken = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(deviceKey.ToLowerInvariant()), "deviceKey" },
            { new StringContent(username), "username" },
            { new StringContent(password), "password" }
        };
        if (!string.IsNullOrWhiteSpace(companyToken))
        {
            form.Add(new StringContent(companyToken), "companyToken");
        }

        return form;
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }
}
