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
        return group;
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

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
