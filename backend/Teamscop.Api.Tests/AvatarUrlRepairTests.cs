using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Teamscop.Api.Data;
using Teamscop.Api.Options;
using Teamscop.Api.Services;

namespace Teamscop.Api.Tests;

/// <summary>
/// Defect 5 — "even though admin and staff uploaded the photo when registering, in admin side, no
/// picture is seen."
///
/// The company row and the admin row were written minutes before B12 moved avatars from
/// <c>/media/avatars/…</c> to the authenticated <c>/api/media/avatars/…</c>, and nothing rewrote
/// them. The old path does not fail loudly: behind nginx it falls through to the catch-all banner
/// and answers <c>200 text/plain</c>, so the desktop gets a successful non-image response and shows
/// a placeholder forever. The image is intact on disk; only the stored prefix is stale.
///
/// The sweep runs itself at start-up — the owner must never have to open psql — so what these
/// assert is that it repairs what it should, refuses to touch what it should not, and is safe to
/// run on every boot from here on.
///
/// Each case gets its own isolated database: the sweep is a whole-table pass, and the suite's
/// shared in-memory store is written by every other test class in parallel.
/// </summary>
public class AvatarUrlRepairTests
{
    private const string Base = "/api/media/avatars";

    [Fact]
    public async Task LegacyPrefixedRows_AreRehomedOntoTheConfiguredBasePath()
    {
        await using var db = NewDb();
        var (companyId, userId) = Seed(db, "/media/avatars/79383d0f074f46908f95a9627d817430.png");
        await db.SaveChangesAsync();

        var repaired = await Repair(db).RunOnceAsync(default);

        Assert.Equal(2, repaired);
        Assert.Equal(
            "/api/media/avatars/79383d0f074f46908f95a9627d817430.png",
            (await db.Users.FirstAsync(u => u.Id == userId)).AvatarUrl);
        Assert.Equal(
            "/api/media/avatars/79383d0f074f46908f95a9627d817430.png",
            (await db.Companies.FirstAsync(c => c.Id == companyId)).AvatarUrl);
    }

    [Fact]
    public async Task RunningItAgain_ChangesNothing()
    {
        await using var db = NewDb();
        var (_, userId) = Seed(db, "/media/avatars/ca643c846ddf4ff69513d95a415ebcc1.png");
        await db.SaveChangesAsync();

        Assert.Equal(2, await Repair(db).RunOnceAsync(default));
        var afterFirst = (await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId)).AvatarUrl;

        // It runs on every boot, so "safe to repeat" is the load-bearing property, not a nicety.
        Assert.Equal(0, await Repair(db).RunOnceAsync(default));
        Assert.Equal(0, await Repair(db).RunOnceAsync(default));
        Assert.Equal(afterFirst, (await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId)).AvatarUrl);
        Assert.Equal(Base + "/ca643c846ddf4ff69513d95a415ebcc1.png", afterFirst);
    }

    [Fact]
    public async Task RowsAlreadyOnTheConfiguredPath_AreLeftAlone()
    {
        await using var db = NewDb();
        var (_, userId) = Seed(db, Base + "/already.png");
        await db.SaveChangesAsync();

        Assert.Equal(0, await Repair(db).RunOnceAsync(default));
        Assert.Equal(Base + "/already.png", (await db.Users.FirstAsync(u => u.Id == userId)).AvatarUrl);
    }

    [Fact]
    public async Task NullAvatars_SurviveUntouched()
    {
        await using var db = NewDb();
        var (companyId, userId) = Seed(db, avatarUrl: null);
        await db.SaveChangesAsync();

        Assert.Equal(0, await Repair(db).RunOnceAsync(default));
        Assert.Null((await db.Users.FirstAsync(u => u.Id == userId)).AvatarUrl);
        Assert.Null((await db.Companies.FirstAsync(c => c.Id == companyId)).AvatarUrl);
    }

    [Theory]
    [InlineData("https://cdn.example.com/media/avatars/x.png")]  // not ours to re-point
    [InlineData("/media/portraits/x.png")]                       // not an avatars directory
    [InlineData("/media/avatars/")]                              // no file name
    [InlineData("/media/avatars/../../etc/passwd")]              // never rewrite a traversal
    public async Task ValuesItDoesNotUnderstand_AreLeftExactlyAsFound(string stored)
    {
        await using var db = NewDb();
        var (_, userId) = Seed(db, stored);
        await db.SaveChangesAsync();

        Assert.Equal(0, await Repair(db).RunOnceAsync(default));
        Assert.Equal(stored, (await db.Users.FirstAsync(u => u.Id == userId)).AvatarUrl);
    }

    [Fact]
    public async Task ChangingTheConfiguredBasePath_RehomesTheRowsAgain()
    {
        await using var db = NewDb();
        var (_, userId) = Seed(db, "/media/avatars/moved.png");
        await db.SaveChangesAsync();

        await Repair(db).RunOnceAsync(default);
        Assert.Equal(2, await Repair(db, "/api/v2/avatars").RunOnceAsync(default));
        Assert.Equal("/api/v2/avatars/moved.png", (await db.Users.FirstAsync(u => u.Id == userId)).AvatarUrl);
    }

    private static (Guid CompanyId, Guid UserId) Seed(AppDbContext db, string? avatarUrl)
    {
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        db.Companies.Add(new Company
        {
            Id = companyId,
            Name = "Repair Co",
            AvatarUrl = avatarUrl,
            TokenJti = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.Users.Add(new UserAccount
        {
            Id = userId,
            CompanyId = companyId,
            DeviceKey = Guid.NewGuid().ToString("N"),
            Username = "Repair Admin",
            PasswordHash = "x",
            Role = UserRole.Admin,
            AvatarUrl = avatarUrl,
            CreatedAt = DateTimeOffset.UtcNow
        });
        return (companyId, userId);
    }

    private static AvatarUrlRepair Repair(AppDbContext db, string basePath = Base)
        => new(
            db,
            Microsoft.Extensions.Options.Options.Create(new StorageOptions { PublicAvatarBasePath = basePath }),
            NullLogger<AvatarUrlRepair>.Instance);

    private static AppDbContext NewDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("avatar-repair-" + Guid.NewGuid().ToString("N"))
            .Options);
}

/// <summary>
/// The other half of why the sweep is sufficient: <see cref="AvatarAccess"/> resolves an avatar's
/// owner by FILE NAME, never by the whole stored URL. That is what lets a repaired row and an
/// original row behave identically — and it means a row the sweep has not reached yet still
/// serves. Nothing asserted it, so nothing stopped a future change from matching on the full URL
/// and quietly breaking every pre-B12 row again.
/// </summary>
public sealed class LegacyAvatarUrlResolutionTests(AvatarApiFactory factory) : IClassFixture<AvatarApiFactory>
{
    [Fact]
    public async Task AStoredLegacyPrefix_StillResolvesThroughTheAuthenticatedRoute()
    {
        var client = factory.CreateClient();
        var (adminToken, companyToken, _) = await AvatarFixture.SignupAdminAsync(client);
        var staff = await AvatarFixture.SignupStaffAsync(client, companyToken);

        var fileName = staff.AvatarUrl[(staff.AvatarUrl.LastIndexOf('/') + 1)..];

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Users.FirstAsync(u => u.Id == staff.Id);
            row.AvatarUrl = "/media/avatars/" + fileName;
            await db.SaveChangesAsync();
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/media/avatars/" + fileName);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("image/png", resp.Content.Headers.ContentType?.MediaType);
    }
}
