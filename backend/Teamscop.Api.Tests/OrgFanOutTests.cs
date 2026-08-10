using System.Data.Common;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Teamscop.Api.Data;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.Api.Tests;

/// <summary>
/// B5 — the org-change fan-out. Two guards, because the fix has two halves that can break
/// independently: every staff member must still be told their new effective authorities, and
/// telling them must not cost a query each.
/// </summary>
public class OrgFanOutBroadcastTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public OrgFanOutBroadcastTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(_ => { });
        _client = _factory.CreateClient();
    }

    /// <summary>
    /// The behaviour the batching must preserve: a leader gains inherent view packages the moment
    /// they are assigned, and everyone else in the company is still pushed their own set.
    /// </summary>
    [Fact]
    public async Task OrgChange_PushesAuthoritiesUpdated_ToEveryStaffMember()
    {
        var (adminToken, companyToken) = await SignupAdminAsync();
        var (leaderId, leaderToken, _) = await SignupStaffAsync(companyToken);
        var (memberId, memberToken, _) = await SignupStaffAsync(companyToken);
        var (_, bystanderToken, _) = await SignupStaffAsync(companyToken);

        await using var leaderConn = Hub(leaderToken);
        await using var memberConn = Hub(memberToken);
        await using var bystanderConn = Hub(bystanderToken);

        var leaderPush = Listen(leaderConn);
        var memberPush = Listen(memberConn);
        var bystanderPush = Listen(bystanderConn);

        await leaderConn.StartAsync();
        await memberConn.StartAsync();
        await bystanderConn.StartAsync();

        using var createReq = Authed(HttpMethod.Post, "/api/teams", adminToken);
        createReq.Content = JsonContent.Create(new { name = "FanOut", leaderUserId = leaderId });
        var createResp = await _client.SendAsync(createReq);
        createResp.EnsureSuccessStatusCode();
        var teamId = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("teamId").GetGuid();

        using var memReq = Authed(HttpMethod.Put, $"/api/teams/{teamId}/members", adminToken);
        memReq.Content = JsonContent.Create(new { memberUserIds = new[] { memberId } });
        (await _client.SendAsync(memReq)).EnsureSuccessStatusCode();

        var all = Task.WhenAll(leaderPush.Task, memberPush.Task, bystanderPush.Task);
        var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(
            finished == all,
            "Every staff member in the company must receive AuthoritiesUpdated after an org change — "
            + $"leader={leaderPush.Task.IsCompleted} member={memberPush.Task.IsCompleted} "
            + $"bystander={bystanderPush.Task.IsCompleted}. Batching the fan-out must not drop anyone.");

        var leaderAuth = await leaderPush.Task;
        Assert.Equal(leaderId, leaderAuth.UserId);
        // A leader inherently holds timetrack and screenshots (team-scoped). Browsing history is
        // NOT inherent — it stays an explicit grant.
        Assert.Contains(AuthorityPackageIds.ViewTimeTrack, leaderAuth.Packages);
        Assert.Contains(AuthorityPackageIds.ViewScreenshot, leaderAuth.Packages);
        Assert.DoesNotContain(AuthorityPackageIds.ViewBrowserHistory, leaderAuth.Packages);
        Assert.DoesNotContain(AuthorityPackageIds.UsbApproval, leaderAuth.Packages);
        Assert.False(leaderAuth.IsAdmin);

        // A plain member inherits nothing from the same broadcast.
        var memberAuth = await memberPush.Task;
        Assert.Equal(memberId, memberAuth.UserId);
        Assert.Empty(memberAuth.Packages);

        await leaderConn.StopAsync();
        await memberConn.StopAsync();
        await bystanderConn.StopAsync();
    }

    /// <summary>
    /// Losing leadership must be pushed as immediately as gaining it, or a demoted leader keeps a
    /// workspace they may no longer use until they restart the app.
    /// </summary>
    [Fact]
    public async Task LeaderCleared_IsPushedTheirNarrowedAuthorities()
    {
        var (adminToken, companyToken) = await SignupAdminAsync();
        var (leaderId, leaderToken, _) = await SignupStaffAsync(companyToken);

        using var createReq = Authed(HttpMethod.Post, "/api/teams", adminToken);
        createReq.Content = JsonContent.Create(new { name = "Demote", leaderUserId = leaderId });
        var createResp = await _client.SendAsync(createReq);
        createResp.EnsureSuccessStatusCode();
        var teamId = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("teamId").GetGuid();

        await using var leaderConn = Hub(leaderToken);
        var push = Listen(leaderConn);
        await leaderConn.StartAsync();

        using var clearReq = Authed(HttpMethod.Put, $"/api/teams/{teamId}", adminToken);
        clearReq.Content = JsonContent.Create(new { clearLeader = true });
        (await _client.SendAsync(clearReq)).EnsureSuccessStatusCode();

        var finished = await Task.WhenAny(push.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(finished == push.Task, "A cleared leader must be pushed their narrowed authorities.");

        var auth = await push.Task;
        Assert.Equal(leaderId, auth.UserId);
        Assert.Empty(auth.Packages);
    }

    private static TaskCompletionSource<EffectiveAuthoritiesDto> Listen(HubConnection connection)
    {
        var tcs = new TaskCompletionSource<EffectiveAuthoritiesDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<EffectiveAuthoritiesDto>("AuthoritiesUpdated", auth => tcs.TrySetResult(auth));
        return tcs;
    }

    private HubConnection Hub(string token)
        => new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress!, "hubs/config"), o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

    private async Task<(string AccessToken, string CompanyToken)> SignupAdminAsync()
        => await OrgFanOutSignup.AdminAsync(_client);

    private async Task<(Guid Id, string AccessToken, string DeviceKey)> SignupStaffAsync(string companyToken)
        => await OrgFanOutSignup.StaffAsync(_client, companyToken);

    private static HttpRequestMessage Authed(HttpMethod method, string url, string token)
        => OrgFanOutSignup.Authed(method, url, token);
}

/// <summary>
/// The API on a throwaway PostgreSQL, with every SQL command counted. The in-memory provider
/// issues no commands at all, so this is the only place a query count can be observed.
/// </summary>
public sealed class QueryCountingPostgresApiFactory : PostgresApiFactory
{
    public CountingCommandInterceptor Commands { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // The retention job would otherwise wake mid-measurement and add commands of its own.
        builder.UseSetting("Retention:AgentEventsDays", "0");
        builder.ConfigureServices((context, services) =>
        {
            // AddDbContext registers its options with TryAdd, so a second call is ignored: the
            // original registration has to go before the interceptor can be attached.
            foreach (var descriptor in services.Where(d =>
                         d.ServiceType == typeof(DbContextOptions<AppDbContext>)
                         || d.ServiceType == typeof(DbContextOptions)
                         || d.ServiceType == typeof(AppDbContext)).ToList())
            {
                services.Remove(descriptor);
            }

            var connectionString = context.Configuration.GetConnectionString("Default");
            services.AddDbContext<AppDbContext>(options =>
                options.UseNpgsql(connectionString).AddInterceptors(Commands));
        });
    }
}

/// <summary>Counts executed SQL commands. Registered as an <see cref="IInterceptor"/> service.</summary>
public sealed class CountingCommandInterceptor : DbCommandInterceptor
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Reset() => Volatile.Write(ref _count, 0);

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Interlocked.Increment(ref _count);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Interlocked.Increment(ref _count);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Interlocked.Increment(ref _count);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>
/// B5, measured rather than argued: one "add member" click must cost the same number of queries in
/// a 30-person company as in a 3-person one. The old fan-out asked <c>IAccessPolicy</c> for each
/// staff member in turn, so this count grew by two per employee.
/// </summary>
public sealed class OrgFanOutQueryCountTests(QueryCountingPostgresApiFactory factory)
    : IClassFixture<QueryCountingPostgresApiFactory>
{
    [PostgresFact]
    public async Task AddMember_QueryCount_DoesNotScaleWithStaffCount()
    {
        var client = factory.CreateClient();

        var small = await MeasureAddMemberAsync(client, staffCount: 3);
        var large = await MeasureAddMemberAsync(client, staffCount: 30);

        Assert.True(small > 0, "The interceptor saw no SQL at all — the measurement is not wired up.");
        Assert.True(
            large == small,
            $"Adding a team member ran {small} queries in a 3-staff company and {large} in a "
            + "30-staff one. The org fan-out must batch its loads, not query per staff member (B5).");
    }

    /// <summary>Builds a company of <paramref name="staffCount"/>, then counts one member add.</summary>
    private async Task<int> MeasureAddMemberAsync(HttpClient client, int staffCount)
    {
        var (adminToken, companyToken) = await OrgFanOutSignup.AdminAsync(client);
        var staffIds = new List<Guid>();
        for (var i = 0; i < staffCount; i++)
        {
            var (id, _, _) = await OrgFanOutSignup.StaffAsync(client, companyToken);
            staffIds.Add(id);
        }

        using var createReq = OrgFanOutSignup.Authed(HttpMethod.Post, "/api/teams", adminToken);
        createReq.Content = JsonContent.Create(new { name = "Counted", leaderUserId = staffIds[0] });
        var createResp = await client.SendAsync(createReq);
        createResp.EnsureSuccessStatusCode();
        var teamId = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("teamId").GetGuid();

        using var addReq = OrgFanOutSignup.Authed(HttpMethod.Post, $"/api/teams/{teamId}/members", adminToken);
        addReq.Content = JsonContent.Create(new { staffUserId = staffIds[1] });

        factory.Commands.Reset();
        (await client.SendAsync(addReq)).EnsureSuccessStatusCode();
        return factory.Commands.Count;
    }
}

/// <summary>Signup helpers shared by the two fan-out fixtures.</summary>
internal static class OrgFanOutSignup
{
    public static async Task<(string AccessToken, string CompanyToken)> AdminAsync(HttpClient client)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(Device()), "deviceKey" },
            { new StringContent("FanOut Co " + Guid.NewGuid().ToString("N")[..8]), "username" },
            { new StringContent("password123"), "password" }
        };
        var resp = await client.PostAsync("/api/auth/admin/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("accessToken").GetString()!,
            doc.RootElement.GetProperty("companyToken").GetString()!);
    }

    public static async Task<(Guid Id, string AccessToken, string DeviceKey)> StaffAsync(
        HttpClient client, string companyToken)
    {
        var device = Device();
        using var form = new MultipartFormDataContent
        {
            { new StringContent(device), "deviceKey" },
            { new StringContent("Staff " + Guid.NewGuid().ToString("N")[..8]), "username" },
            { new StringContent("password123"), "password" },
            { new StringContent(companyToken), "companyToken" }
        };
        var resp = await client.PostAsync("/api/auth/staff/signup", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("user").GetProperty("id").GetGuid(),
            doc.RootElement.GetProperty("accessToken").GetString()!,
            device);
    }

    public static HttpRequestMessage Authed(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static string Device() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
}
