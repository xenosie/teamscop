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
        group.MapPut("/config/{staffUserId:guid}", UpsertConfigAsync).RequireAuthorization();
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

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
