using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Teamscop.Api.Data;
using Teamscop.Api.Services;

namespace Teamscop.Api.Tests;

/// <summary>
/// The two new queries that run against the deployed database rather than a test's in-memory
/// store, proved against real SQL (C1).
///
/// The avatar sweep matters most: it runs once at every API start-up and its whole job is to touch
/// production rows. An untranslatable predicate would not crash anything — the hosted service
/// catches and logs — it would simply never repair, and the owner would still be looking at
/// placeholders while a green test suite said otherwise. That is precisely the failure shape this
/// whole workflow exists to stop.
/// </summary>
public sealed class BackendRepairPostgresTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    [PostgresFact]
    public async Task TheAvatarSweep_RepairsALegacyRow_AgainstRealSql()
    {
        var client = factory.CreateClient();
        var (adminToken, _) = await SignupAdminAsync(client);
        var adminId = await MyUserIdAsync(client, adminToken);

        const string fileName = "79383d0f074f46908f95a9627d817430.png";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var admin = await db.Users.FirstAsync(u => u.Id == adminId);
            admin.AvatarUrl = "/media/avatars/" + fileName;
            var company = await db.Companies.FirstAsync(c => c.Id == admin.CompanyId);
            company.AvatarUrl = "/media/avatars/" + fileName;
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var repaired = await scope.ServiceProvider
                .GetRequiredService<IAvatarUrlRepair>()
                .RunOnceAsync(default);
            Assert.True(repaired >= 2, $"the sweep rewrote {repaired} row(s); it must reach both.");
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var admin = await db.Users.AsNoTracking().FirstAsync(u => u.Id == adminId);
            Assert.Equal("/api/media/avatars/" + fileName, admin.AvatarUrl);
            var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == admin.CompanyId);
            Assert.Equal("/api/media/avatars/" + fileName, company.AvatarUrl);

            // And re-running it against the repaired rows must be a no-op, not a second rewrite.
            var again = await scope.ServiceProvider
                .GetRequiredService<IAvatarUrlRepair>()
                .RunOnceAsync(default);
            Assert.Equal(0, again);
        }
    }

    [PostgresFact]
    public async Task TheCodesRoster_IsStaffOnly_AndExcludesTheAdminMachine_AgainstRealSql()
    {
        var client = factory.CreateClient();
        var (adminToken, companyToken) = await SignupAdminAsync(client);
        var adminId = await MyUserIdAsync(client, adminToken);
        var staffId = await SignupStaffAsync(client, companyToken);

        // §1.5 — the Codes screen is staff only. The admin's own machine is not monitored and
        // removes its own service (§8.2), so it is never a code target and never listed.
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/lifecycle/totp/staff");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var rows = doc.RootElement.EnumerateArray().ToList();

        Assert.DoesNotContain(rows, r => r.GetProperty("staffUserId").GetGuid() == adminId);

        var staff = Assert.Single(rows, r => r.GetProperty("staffUserId").GetGuid() == staffId);
        Assert.Equal("staff", staff.GetProperty("role").GetString());
        Assert.False(staff.GetProperty("isSelfMachine").GetBoolean());
    }

    private static async Task<Guid> MyUserIdAsync(HttpClient client, string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<(string AccessToken, string CompanyToken)> SignupAdminAsync(HttpClient client)
    {
        var device = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device), "deviceKey" },
            { new StringContent("PgRepair " + Guid.NewGuid().ToString("N")[..6]), "username" },
            { new StringContent("password123"), "password" }
        };
        var resp = await client.PostAsync("/api/auth/admin/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("accessToken").GetString()!,
            doc.RootElement.GetProperty("companyToken").GetString()!);
    }

    private static async Task<Guid> SignupStaffAsync(HttpClient client, string companyToken)
    {
        var device = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device), "deviceKey" },
            { new StringContent("Staff" + Guid.NewGuid().ToString("N")[..6]), "username" },
            { new StringContent("password123"), "password" },
            { new StringContent(companyToken), "companyToken" }
        };
        var resp = await client.PostAsync("/api/auth/staff/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("user").GetProperty("id").GetGuid();
    }
}
