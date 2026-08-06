using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Teamscop.Api.Services;
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
        group.MapGet("/events", QueryEventsAsync).RequireAuthorization();
        group.MapGet("/screenshots", ListScreenshotsAsync).RequireAuthorization();
        group.MapGet("/screenshots/{eventId:guid}/thumb", GetScreenshotThumbAsync).RequireAuthorization();
        group.MapGet("/screenshots/{eventId:guid}/image", GetScreenshotImageAsync).RequireAuthorization();
        group.MapGet("/browsing", ListBrowsingDomainsAsync).RequireAuthorization();
        group.MapGet("/browsing/detail", GetBrowsingDomainDetailAsync).RequireAuthorization();
        group.MapGet("/browsing/top-urls", ListBrowsingTopUrlsAsync).RequireAuthorization();
        group.MapGet("/timetrack", GetTimeTrackTimelineAsync).RequireAuthorization();
        group.MapGet("/chain/{staffUserId:guid}", GetChainHealthAsync).RequireAuthorization();
        return group;
    }

    private static async Task<IResult> GetChainHealthAsync(
        Guid staffUserId,
        ClaimsPrincipal principal,
        IChainHealthService chain,
        CancellationToken ct)
    {
        var viewerId = GetUserId(principal);
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            return Results.Ok(await chain.GetAsync(viewerId.Value, staffUserId, ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> GetMyConfigAsync(
        ClaimsPrincipal principal,
        ITrackingConfigService configs,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            return Results.Ok(await configs.GetForStaffAsync(userId.Value, ct));
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
    }

    private static async Task<IResult> GetStaffConfigAsync(
        Guid staffUserId,
        ClaimsPrincipal principal,
        ITrackingQueryService query,
        CancellationToken ct)
    {
        var viewerId = GetUserId(principal);
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            return Results.Ok(await query.GetConfigIfAllowedAsync(viewerId.Value, staffUserId, ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    private static async Task<IResult> UpsertConfigAsync(
        Guid staffUserId,
        [FromBody] StaffTrackingConfig body,
        ClaimsPrincipal principal,
        ITrackingConfigService configs,
        CancellationToken ct)
    {
        var adminId = GetUserId(principal);
        if (adminId is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            var updated = await configs.UpsertByAdminAsync(adminId.Value, staffUserId, body, ct);
            return Results.Ok(updated);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> ListVisibleStaffAsync(
        ClaimsPrincipal principal,
        ITeamService teams,
        CancellationToken ct)
    {
        var viewerId = GetUserId(principal);
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            return Results.Ok(await teams.ListVisibleStaffAsync(viewerId.Value, ct));
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
    }

    private static async Task<IResult> QueryEventsAsync(
        [FromQuery] Guid staffUserId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? eventType,
        [FromQuery] int? take,
        ClaimsPrincipal principal,
        ITrackingQueryService query,
        CancellationToken ct)
    {
        var viewerId = GetUserId(principal);
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            var events = await query.QueryEventsAsync(
                viewerId.Value, staffUserId, from, to, eventType, take ?? 100, ct);
            return Results.Ok(events);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    private static async Task<IResult> ListScreenshotsAsync(
        [FromQuery] Guid staffUserId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? take,
        ClaimsPrincipal principal,
        IScreenshotMediaService screenshots,
        CancellationToken ct)
    {
        var viewerId = GetUserId(principal);
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            var items = await screenshots.ListAsync(
                viewerId.Value, staffUserId, from, to, take ?? 100, ct);
            return Results.Ok(items);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    private static async Task<IResult> GetScreenshotThumbAsync(
        Guid eventId,
        [FromQuery] int? display,
        [FromQuery] int? w,
        ClaimsPrincipal principal,
        IScreenshotMediaService screenshots,
        CancellationToken ct)
    {
        var viewerId = GetUserId(principal);
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            var bytes = await screenshots.GetThumbJpegAsync(
                viewerId.Value, eventId, display ?? 1, w ?? 320, ct);
            if (bytes is null)
            {
                return Results.NotFound(new { error = "Screenshot not found." });
            }

            return WithPrivateCache(Results.File(bytes, "image/jpeg", enableRangeProcessing: false));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    private static async Task<IResult> GetScreenshotImageAsync(
        Guid eventId,
        [FromQuery] int? display,
        ClaimsPrincipal principal,
        IScreenshotMediaService screenshots,
        CancellationToken ct)
    {
        var viewerId = GetUserId(principal);
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            var bytes = await screenshots.GetFullJpegAsync(
                viewerId.Value, eventId, display ?? 1, ct);
            if (bytes is null)
            {
                return Results.NotFound(new { error = "Screenshot not found." });
            }

            return WithPrivateCache(Results.File(bytes, "image/jpeg", enableRangeProcessing: false));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    private static async Task<IResult> ListBrowsingDomainsAsync(
        [FromQuery] Guid staffUserId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? take,
        ClaimsPrincipal principal,
        IBrowsingQueryService browsing,
        CancellationToken ct)
    {
        var viewerId = GetUserId(principal);
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            var items = await browsing.ListDomainsAsync(
                viewerId.Value, staffUserId, from, to, take ?? 200, ct);
            return Results.Ok(items);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    private static async Task<IResult> GetBrowsingDomainDetailAsync(
        [FromQuery] Guid staffUserId,
        [FromQuery] string domain,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? take,
        ClaimsPrincipal principal,
        IBrowsingQueryService browsing,
        CancellationToken ct)
    {
        var viewerId = GetUserId(principal);
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(domain))
        {
            return Results.BadRequest(new { error = "domain is required." });
        }

        try
        {
            var detail = await browsing.GetDomainDetailAsync(
                viewerId.Value, staffUserId, domain, from, to, take ?? 200, ct);
            return detail is null
                ? Results.NotFound(new { error = "Domain not found." })
                : Results.Ok(detail);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    private static async Task<IResult> ListBrowsingTopUrlsAsync(
        [FromQuery] Guid staffUserId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? take,
        [FromQuery] int? takeEvents,
        ClaimsPrincipal principal,
        IBrowsingQueryService browsing,
        CancellationToken ct)
    {
        var viewerId = GetUserId(principal);
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            var items = await browsing.ListTopUrlsAsync(
                viewerId.Value, staffUserId, from, to, take ?? 3, takeEvents ?? 200, ct);
            return Results.Ok(items);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    private static async Task<IResult> GetTimeTrackTimelineAsync(
        [FromQuery] Guid staffUserId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        ClaimsPrincipal principal,
        ITimeTrackQueryService timeTrack,
        CancellationToken ct)
    {
        var viewerId = GetUserId(principal);
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        if (from is null || to is null)
        {
            return Results.BadRequest(new { error = "from and to are required (business-period bounds)." });
        }

        try
        {
            var timeline = await timeTrack.GetTimelineAsync(
                viewerId.Value, staffUserId, from.Value, to.Value, ct);
            return Results.Ok(timeline);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    private static IResult WithPrivateCache(IResult inner)
        => new CachedFileResult(inner, "private, max-age=3600");

    private sealed class CachedFileResult(IResult inner, string cacheControl) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = cacheControl;
            await inner.ExecuteAsync(httpContext);
        }
    }

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
