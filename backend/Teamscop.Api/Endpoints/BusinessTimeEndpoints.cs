using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Teamscop.Api.Services;

namespace Teamscop.Api.Endpoints;

public static class BusinessTimeEndpoints
{
    public static RouteGroupBuilder MapBusinessTimeEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/business-time").WithTags("BusinessTime");
        group.MapGet("/me", GetMineAsync).RequireAuthorization();
        group.MapPost("/declare", DeclareAsync).RequireAuthorization();
        group.MapGet("/now", GetNowAsync).RequireAuthorization();
        return group;
    }

    private static async Task<IResult> GetMineAsync(
        ClaimsPrincipal principal,
        IBusinessTimeService businessTime,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            return Results.Ok(await businessTime.GetForUserAsync(userId.Value, ct));
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
    }

    private static async Task<IResult> DeclareAsync(
        [FromBody] DeclareBusinessTimeRequest body,
        ClaimsPrincipal principal,
        IBusinessTimeService businessTime,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            var cfg = await businessTime.DeclareSyncAsync(userId.Value, body, ct);
            return Results.Ok(cfg);
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

    private static async Task<IResult> GetNowAsync(
        ClaimsPrincipal principal,
        IBusinessTimeService businessTime,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            var cfg = await businessTime.GetForUserAsync(userId.Value, ct);
            var clock = new Teamscop.Engine.Tracking.BusinessClock();
            clock.Apply(cfg);
            var now = clock.Now();
            return Results.Ok(new
            {
                cfg.CompanyId,
                cfg.TimeZoneId,
                cfg.ClockVersion,
                cfg.IsSynchronized,
                utc = now.Utc,
                businessLocal = now.BusinessLocalIso,
                synchronized = now.Synchronized
            });
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
    }

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
