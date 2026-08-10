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
        group.MapGet("/zones", ListZonesAsync).RequireAuthorization();
        // §8.4: picking a timezone replaces the whole clock, so this is a PUT of the one setting.
        group.MapPut("", SetTimeZoneAsync).RequireAuthorization();
        group.MapGet("/now", GetNowAsync).RequireAuthorization();
        return group;
    }

    private static async Task<IResult> GetMineAsync(
        ClaimsPrincipal principal,
        IBusinessTimeService businessTime,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(await businessTime.GetForUserAsync(userId.Value, ct));
    }

    private static IResult ListZonesAsync(
        ClaimsPrincipal principal,
        IBusinessTimeService businessTime)
    {
        var userId = principal.UserId();
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(businessTime.ListTimeZones());
    }

    private static async Task<IResult> SetTimeZoneAsync(
        [FromBody] SetCompanyTimeZoneRequest body,
        ClaimsPrincipal principal,
        IBusinessTimeService businessTime,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(await businessTime.SetTimeZoneAsync(userId.Value, body, ct));
    }

    private static async Task<IResult> GetNowAsync(
        ClaimsPrincipal principal,
        IBusinessTimeService businessTime,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var cfg = await businessTime.GetForUserAsync(userId.Value, ct);
        var zone = CompanyBusinessTime.Resolve(cfg.TimeZoneId);
        var utc = DateTimeOffset.UtcNow;
        return Results.Ok(new
        {
            cfg.CompanyId,
            cfg.TimeZoneId,
            utc,
            businessLocal = CompanyBusinessTime.ToBusinessLocalIso(utc, zone)
        });
    }
}
