using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Teamscop.Api.Tests;

/// <summary>The API with its avatar store pointed at a scratch directory.</summary>
public sealed class AvatarApiFactory : WebApplicationFactory<Program>
{
    private readonly string _avatarRoot =
        Path.Combine(Path.GetTempPath(), "teamscop-avatars-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Configured with a trailing separator on purpose: the route's containment check compares
    /// composed paths against the root, and a trailing separator is the obvious way to break it.
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.UseSetting("Storage:AvatarRoot", _avatarRoot + Path.DirectorySeparatorChar);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_avatarRoot))
        {
            Directory.Delete(_avatarRoot, recursive: true);
        }
    }
}

/// <summary>The same, on a throwaway PostgreSQL — resolving an avatar's owner is a SQL query (C1).</summary>
public sealed class AvatarPostgresApiFactory : PostgresApiFactory
{
    private readonly string _avatarRoot =
        Path.Combine(Path.GetTempPath(), "teamscop-pg-avatars-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Storage:AvatarRoot", _avatarRoot);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_avatarRoot))
        {
            Directory.Delete(_avatarRoot, recursive: true);
        }
    }
}

/// <summary>
/// B12 — an avatar is staff data. It must need a token, and the token must belong to someone
/// entitled to that particular employee: their own, their company's, or anyone they may view.
/// </summary>
public sealed class AvatarAccessTests(AvatarApiFactory factory) : IClassFixture<AvatarApiFactory>
{
    [Fact]
    public async Task Avatar_IsNotReadableWithoutAToken_AndNoLongerSitsOnAPublicPath()
    {
        var client = factory.CreateClient();
        var (_, companyToken, _) = await AvatarFixture.SignupAdminAsync(client);
        var staff = await AvatarFixture.SignupStaffAsync(client, companyToken);

        Assert.StartsWith("/api/media/avatars/", staff.AvatarUrl);

        var anonymous = await client.GetAsync(staff.AvatarUrl);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // The static path the file used to be served from must be gone, not merely unadvertised.
        var fileName = staff.AvatarUrl[(staff.AvatarUrl.LastIndexOf('/') + 1)..];
        var legacy = await client.GetAsync("/media/avatars/" + fileName);
        Assert.Equal(HttpStatusCode.NotFound, legacy.StatusCode);
    }

    [Fact]
    public async Task Staff_ReadTheirOwnFaceAndTheirCompanyBadge_ButNotAColleaguesFace()
    {
        var client = factory.CreateClient();
        var (_, companyToken, companyAvatarUrl) = await AvatarFixture.SignupAdminAsync(client);
        var staff = await AvatarFixture.SignupStaffAsync(client, companyToken);
        var colleague = await AvatarFixture.SignupStaffAsync(client, companyToken);

        Assert.Equal(HttpStatusCode.OK, await AvatarFixture.StatusAsync(client, staff.AvatarUrl, staff.Token));
        Assert.Equal(HttpStatusCode.OK, await AvatarFixture.StatusAsync(client, companyAvatarUrl, staff.Token));

        // §4.4 — plain staff see their own sticker and nothing about anyone else.
        Assert.Equal(
            HttpStatusCode.NotFound,
            await AvatarFixture.StatusAsync(client, colleague.AvatarUrl, staff.Token));
    }

    [Fact]
    public async Task Admin_ReadsAnyStaffFace_AndTheContentTypeSurvives()
    {
        var client = factory.CreateClient();
        var (adminToken, companyToken, _) = await AvatarFixture.SignupAdminAsync(client);
        var staff = await AvatarFixture.SignupStaffAsync(client, companyToken);

        using var req = AvatarFixture.Authed(HttpMethod.Get, staff.AvatarUrl, adminToken);
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("image/png", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal(AvatarFixture.Png, await resp.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", resp.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task AnotherCompanysAvatar_IsNotFound()
    {
        var client = factory.CreateClient();
        var (_, companyTokenA, companyAvatarA) = await AvatarFixture.SignupAdminAsync(client);
        var staffA = await AvatarFixture.SignupStaffAsync(client, companyTokenA);

        var (adminB, companyTokenB, _) = await AvatarFixture.SignupAdminAsync(client);
        var staffB = await AvatarFixture.SignupStaffAsync(client, companyTokenB);

        Assert.Equal(HttpStatusCode.NotFound, await AvatarFixture.StatusAsync(client, staffA.AvatarUrl, adminB));
        Assert.Equal(
            HttpStatusCode.NotFound,
            await AvatarFixture.StatusAsync(client, companyAvatarA, staffB.Token));
    }

    [Fact]
    public async Task Leader_ReadsTheirTeamsFaces_AndStopsTheMomentTheTeamIsTakenAway()
    {
        var client = factory.CreateClient();
        var (adminToken, companyToken, _) = await AvatarFixture.SignupAdminAsync(client);
        var leader = await AvatarFixture.SignupStaffAsync(client, companyToken);
        var member = await AvatarFixture.SignupStaffAsync(client, companyToken);
        var outsider = await AvatarFixture.SignupStaffAsync(client, companyToken);

        using var createReq = AvatarFixture.Authed(HttpMethod.Post, "/api/teams", adminToken);
        createReq.Content = JsonContent.Create(new { name = "Faces", leaderUserId = leader.Id });
        var createResp = await client.SendAsync(createReq);
        createResp.EnsureSuccessStatusCode();
        var teamId = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("teamId").GetGuid();

        using var memReq = AvatarFixture.Authed(HttpMethod.Put, $"/api/teams/{teamId}/members", adminToken);
        memReq.Content = JsonContent.Create(new { memberUserIds = new[] { member.Id } });
        (await client.SendAsync(memReq)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, await AvatarFixture.StatusAsync(client, member.AvatarUrl, leader.Token));
        Assert.Equal(
            HttpStatusCode.NotFound,
            await AvatarFixture.StatusAsync(client, outsider.AvatarUrl, leader.Token));

        // Demotion must land immediately: the cached reach is dropped by the org-change path,
        // not left to expire.
        using var clearReq = AvatarFixture.Authed(HttpMethod.Put, $"/api/teams/{teamId}", adminToken);
        clearReq.Content = JsonContent.Create(new { clearLeader = true });
        (await client.SendAsync(clearReq)).EnsureSuccessStatusCode();

        Assert.Equal(
            HttpStatusCode.NotFound,
            await AvatarFixture.StatusAsync(client, member.AvatarUrl, leader.Token));
    }

    [Theory]
    [InlineData("../appsettings.json")]
    [InlineData("..%2Fappsettings.json")]
    [InlineData("nonexistent00000000000000000000.png")]
    [InlineData("secret.txt")]
    public async Task UnservableNames_NeverReturnAFile(string fileName)
    {
        var client = factory.CreateClient();
        var (adminToken, _, _) = await AvatarFixture.SignupAdminAsync(client);

        var status = await AvatarFixture.StatusAsync(client, "/api/media/avatars/" + fileName, adminToken);
        Assert.True(
            status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
            $"'{fileName}' answered {(int)status}; the avatar route must serve nothing but avatars.");
    }
}

/// <summary>
/// Resolving an avatar's owner is a <c>LIKE</c> against a nullable column — exactly the shape of
/// query the in-memory provider will happily run and PostgreSQL might not (C1).
/// </summary>
public sealed class AvatarAccessPostgresTests(AvatarPostgresApiFactory factory)
    : IClassFixture<AvatarPostgresApiFactory>
{
    [PostgresFact]
    public async Task AvatarScoping_HoldsAgainstRealSql()
    {
        var client = factory.CreateClient();
        var (adminToken, companyToken, companyAvatarUrl) = await AvatarFixture.SignupAdminAsync(client);
        var staff = await AvatarFixture.SignupStaffAsync(client, companyToken);
        var colleague = await AvatarFixture.SignupStaffAsync(client, companyToken);

        Assert.Equal(HttpStatusCode.OK, await AvatarFixture.StatusAsync(client, staff.AvatarUrl, adminToken));
        Assert.Equal(HttpStatusCode.OK, await AvatarFixture.StatusAsync(client, staff.AvatarUrl, staff.Token));
        Assert.Equal(HttpStatusCode.OK, await AvatarFixture.StatusAsync(client, companyAvatarUrl, staff.Token));
        Assert.Equal(
            HttpStatusCode.NotFound,
            await AvatarFixture.StatusAsync(client, colleague.AvatarUrl, staff.Token));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(staff.AvatarUrl)).StatusCode);
    }
}

/// <summary>Signup helpers shared by the in-memory and PostgreSQL avatar suites.</summary>
internal static class AvatarFixture
{
    /// <summary>An eight-byte PNG header. Nothing decodes it; only the extension is inspected.</summary>
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static async Task<HttpStatusCode> StatusAsync(HttpClient client, string url, string token)
    {
        using var req = Authed(HttpMethod.Get, url, token);
        var resp = await client.SendAsync(req);
        return resp.StatusCode;
    }

    public static async Task<(string Token, string CompanyToken, string CompanyAvatarUrl)> SignupAdminAsync(
        HttpClient client)
    {
        using var form = Form();
        form.Add(new StringContent("Avatar Co " + Guid.NewGuid().ToString("N")[..8]), "username");
        var resp = await client.PostAsync("/api/auth/admin/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var user = doc.RootElement.GetProperty("user");
        return (doc.RootElement.GetProperty("accessToken").GetString()!,
            doc.RootElement.GetProperty("companyToken").GetString()!,
            user.GetProperty("company").GetProperty("avatarUrl").GetString()!);
    }

    public static async Task<(Guid Id, string Token, string AvatarUrl)> SignupStaffAsync(
        HttpClient client, string companyToken)
    {
        using var form = Form();
        form.Add(new StringContent("Face " + Guid.NewGuid().ToString("N")[..8]), "username");
        form.Add(new StringContent(companyToken), "companyToken");
        var resp = await client.PostAsync("/api/auth/staff/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var user = doc.RootElement.GetProperty("user");
        return (user.GetProperty("id").GetGuid(),
            doc.RootElement.GetProperty("accessToken").GetString()!,
            user.GetProperty("avatarUrl").GetString()!);
    }

    public static HttpRequestMessage Authed(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    /// <summary>Device key, password and a PNG — everything a signup needs bar the name.</summary>
    private static MultipartFormDataContent Form()
    {
        var image = new ByteArrayContent(Png);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new MultipartFormDataContent
        {
            { new StringContent(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")), "deviceKey" },
            { new StringContent("password123"), "password" },
            { image, "avatar", "face.png" }
        };
    }
}
