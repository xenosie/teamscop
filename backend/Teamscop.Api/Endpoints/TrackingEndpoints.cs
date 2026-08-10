using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Teamscop.Api.Errors;
using Teamscop.Api.Services;
using Teamscop.Api.Services.Insights;
using Teamscop.Engine.Tracking;

namespace Teamscop.Api.Endpoints;

public static class TrackingEndpoints
{
    public static RouteGroupBuilder MapTrackingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/tracking").WithTags("Tracking");
        group.MapGet("/config/me", GetMyConfigAsync).RequireAuthorization();
        group.MapGet("/config/{staffUserId:guid}", GetStaffConfigAsync).RequireAuthorization();
        group.MapPut("/config/{staffUserId:guid}", UpsertConfigAsync).RequireAuthorization();
        group.MapGet("/staff", ListVisibleStaffAsync).RequireAuthorization();
        group.MapGet("/presence", GetPresenceAsync).RequireAuthorization();
        group.MapGet("/leaderboard", GetLeaderboardAsync).RequireAuthorization();
        group.MapGet("/health/me", GetMyAgentHealthAsync).RequireAuthorization();
        group.MapGet("/events", QueryEventsAsync).RequireAuthorization();
        group.MapGet("/screenshots", ListScreenshotsAsync).RequireAuthorization();
        group.MapGet("/screenshots/{eventId:guid}/thumb", GetScreenshotThumbAsync).RequireAuthorization();
        group.MapGet("/screenshots/{eventId:guid}/image", GetScreenshotImageAsync).RequireAuthorization();
        group.MapGet("/browsing", ListBrowsingDomainsAsync).RequireAuthorization();
        group.MapGet("/browsing/detail", GetBrowsingDomainDetailAsync).RequireAuthorization();
        group.MapGet("/browsing/top-urls", ListBrowsingTopUrlsAsync).RequireAuthorization();
        group.MapGet("/timetrack", GetTimeTrackTimelineAsync).RequireAuthorization();
        return group;
    }

    private static async Task<IResult> GetLeaderboardAsync(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        ClaimsPrincipal principal,
        IBusinessPeriodResolver period,
        IWorkSummaryService summaries,
        CancellationToken ct)
    {
        var viewerId = principal.UserId();
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        // §2.3 — the calendar sends company-local days; the server converts once, here.
        var range = await period.ResolveAsync(viewerId.Value, from, to, ct);
        if (range.From is null || range.To is null)
        {
            return Results.BadRequest(new { error = "from and to are required." });
        }

        return Results.Ok(await summaries.GetLeaderboardAsync(
            viewerId.Value, range.From.Value, range.To.Value, page ?? 0, pageSize ?? 25, ct));
    }

    private static async Task<IResult> GetPresenceAsync(
        ClaimsPrincipal principal,
        IStaffPresenceService presence,
        CancellationToken ct)
    {
        var viewerId = principal.UserId();
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(await presence.GetPresenceAsync(viewerId.Value, ct));
    }

    private static async Task<IResult> GetMyAgentHealthAsync(
        ClaimsPrincipal principal,
        IAgentHealthService health,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(await health.GetSelfAsync(userId.Value, ct));
    }

    private static async Task<IResult> GetMyConfigAsync(
        ClaimsPrincipal principal,
        ITrackingConfigService configs,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(await configs.GetForStaffAsync(userId.Value, ct));
    }

    private static async Task<IResult> GetStaffConfigAsync(
        Guid staffUserId,
        ClaimsPrincipal principal,
        ITrackingQueryService query,
        CancellationToken ct)
    {
        var viewerId = principal.UserId();
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(await query.GetConfigIfAllowedAsync(viewerId.Value, staffUserId, ct));
    }

    private static async Task<IResult> UpsertConfigAsync(
        Guid staffUserId,
        [FromBody] StaffTrackingConfig body,
        ClaimsPrincipal principal,
        ITrackingConfigService configs,
        CancellationToken ct)
    {
        var adminId = principal.UserId();
        if (adminId is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(await configs.UpsertByAdminAsync(adminId.Value, staffUserId, body, ct));
    }

    private static async Task<IResult> ListVisibleStaffAsync(
        ClaimsPrincipal principal,
        ITeamService teams,
        CancellationToken ct)
    {
        var viewerId = principal.UserId();
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(await teams.ListVisibleStaffAsync(viewerId.Value, ct));
    }

    private static async Task<IResult> QueryEventsAsync(
        [FromQuery] Guid staffUserId,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? eventType,
        [FromQuery] int? take,
        ClaimsPrincipal principal,
        IBusinessPeriodResolver period,
        ITrackingQueryService query,
        CancellationToken ct)
    {
        var viewerId = principal.UserId();
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        var range = await period.ResolveAsync(viewerId.Value, from, to, ct);
        return Results.Ok(await query.QueryEventsAsync(
            viewerId.Value, staffUserId, range.From, range.To, eventType, take ?? 100, ct));
    }

    private static async Task<IResult> ListScreenshotsAsync(
        [FromQuery] Guid staffUserId,
        [FromQuery] string? from,
        [FromQuery] string? to,
        // A server-issued opaque cursor (the last tile's occurredAt), never a calendar day, so it
        // stays a UTC instant and is not routed through the day→instant conversion (§2.3).
        [FromQuery] DateTimeOffset? before,
        [FromQuery] int? take,
        ClaimsPrincipal principal,
        IBusinessPeriodResolver period,
        IScreenshotMediaService screenshots,
        CancellationToken ct)
    {
        var viewerId = principal.UserId();
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        var range = await period.ResolveAsync(viewerId.Value, from, to, ct);
        return Results.Ok(await screenshots.ListAsync(
            viewerId.Value, staffUserId, range.From, range.To, before, take ?? 100, ct));
    }

    private static async Task<IResult> GetScreenshotThumbAsync(
        Guid eventId,
        [FromQuery] int? display,
        [FromQuery] int? w,
        ClaimsPrincipal principal,
        IScreenshotMediaService screenshots,
        CancellationToken ct)
    {
        var viewerId = principal.UserId();
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        var thumb = await screenshots.GetThumbAsync(viewerId.Value, eventId, display ?? 1, w ?? 320, ct)
            ?? throw new NotFoundException("Screenshot not found.");
        return WithPrivateCache(Results.File(thumb.Bytes, thumb.ContentType, enableRangeProcessing: false));
    }

    private static async Task<IResult> GetScreenshotImageAsync(
        Guid eventId,
        [FromQuery] int? display,
        ClaimsPrincipal principal,
        IScreenshotMediaService screenshots,
        CancellationToken ct)
    {
        var viewerId = principal.UserId();
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        var image = await screenshots.GetFullAsync(viewerId.Value, eventId, display ?? 1, ct)
            ?? throw new NotFoundException("Screenshot not found.");
        return WithPrivateCache(Results.File(image.Bytes, image.ContentType, enableRangeProcessing: false));
    }

    private static async Task<IResult> ListBrowsingDomainsAsync(
        [FromQuery] Guid staffUserId,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] int? take,
        ClaimsPrincipal principal,
        IBusinessPeriodResolver period,
        IBrowsingQueryService browsing,
        CancellationToken ct)
    {
        var viewerId = principal.UserId();
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        var range = await period.ResolveAsync(viewerId.Value, from, to, ct);
        return Results.Ok(await browsing.ListDomainsAsync(
            viewerId.Value, staffUserId, range.From, range.To, take ?? 200, ct));
    }

    private static async Task<IResult> GetBrowsingDomainDetailAsync(
        [FromQuery] Guid staffUserId,
        [FromQuery] string domain,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] int? take,
        ClaimsPrincipal principal,
        IBusinessPeriodResolver period,
        IBrowsingQueryService browsing,
        CancellationToken ct)
    {
        var viewerId = principal.UserId();
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(domain))
        {
            return Results.BadRequest(new { error = "domain is required." });
        }

        var range = await period.ResolveAsync(viewerId.Value, from, to, ct);
        var detail = await browsing.GetDomainDetailAsync(
                viewerId.Value, staffUserId, domain, range.From, range.To, take ?? 200, ct)
            ?? throw new NotFoundException("Domain not found.");
        return Results.Ok(detail);
    }

    private static async Task<IResult> ListBrowsingTopUrlsAsync(
        [FromQuery] Guid staffUserId,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] int? take,
        [FromQuery] int? takeEvents,
        ClaimsPrincipal principal,
        IBusinessPeriodResolver period,
        IBrowsingQueryService browsing,
        CancellationToken ct)
    {
        var viewerId = principal.UserId();
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        var range = await period.ResolveAsync(viewerId.Value, from, to, ct);
        return Results.Ok(await browsing.ListTopUrlsAsync(
            viewerId.Value, staffUserId, range.From, range.To, take ?? 3, takeEvents ?? 200, ct));
    }

    private static async Task<IResult> GetTimeTrackTimelineAsync(
        [FromQuery] Guid staffUserId,
        [FromQuery] string? from,
        [FromQuery] string? to,
        ClaimsPrincipal principal,
        IBusinessPeriodResolver period,
        ITimeTrackQueryService timeTrack,
        CancellationToken ct)
    {
        var viewerId = principal.UserId();
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        // §2.5 — the bar spans exactly the selected calendar period. These UTC bounds are what the
        // timeline echoes back as From/To, so the bar's domain and the query's domain are one range.
        var range = await period.ResolveAsync(viewerId.Value, from, to, ct);
        if (range.From is null || range.To is null)
        {
            return Results.BadRequest(new { error = "from and to are required (business-period bounds)." });
        }

        return Results.Ok(await timeTrack.GetTimelineAsync(
            viewerId.Value, staffUserId, range.From.Value, range.To.Value, ct));
    }

    // A screenshot never changes once written — it is addressed by event id — so the browser-side
    // cache may keep it indefinitely. "no-store" made the admin's gallery re-download every image
    // on every visit, over the same office uplink the agents upload on. private: it is monitored
    // data behind auth; only the viewer's own machine may keep it.
    private static IResult WithPrivateCache(IResult inner)
        => new CachedFileResult(inner, "private, max-age=31536000, immutable");

    private sealed class CachedFileResult(IResult inner, string cacheControl) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = cacheControl;
            await inner.ExecuteAsync(httpContext);
        }
    }
}
