using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Teamscop.Api.Services;

namespace Teamscop.Api.Endpoints;

public static class PoliceEndpoints
{
    public static RouteGroupBuilder MapPoliceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/police").WithTags("Police");
        group.MapGet("/packages", ListPackagesAsync).RequireAuthorization();
        group.MapGet("/", ListPolicemenAsync).RequireAuthorization();
        group.MapGet("/me", GetMineAsync).RequireAuthorization();
        group.MapPut("/{staffUserId:guid}", UpsertPolicemanAsync).RequireAuthorization();
        group.MapDelete("/{staffUserId:guid}", RevokePolicemanAsync).RequireAuthorization();
        return group;
    }

    private static IResult ListPackagesAsync(IAuthorityService authorities)
        => Results.Ok(authorities.ListPackageCatalog());

    private static async Task<IResult> ListPolicemenAsync(
        ClaimsPrincipal principal, IAuthorityService authorities, CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null) return Results.Unauthorized();
        try
        {
            return Results.Ok(await authorities.ListPolicemenAsync(userId.Value, ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    private static async Task<IResult> GetMineAsync(
        ClaimsPrincipal principal, IAuthorityService authorities, CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null) return Results.Unauthorized();
        try
        {
            return Results.Ok(await authorities.GetEffectiveAsync(userId.Value, ct));
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
    }

    private static async Task<IResult> UpsertPolicemanAsync(
        Guid staffUserId,
        [FromBody] UpsertPoliceBody body,
        ClaimsPrincipal principal,
        IAuthorityService authorities,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null) return Results.Unauthorized();
        try
        {
            var result = await authorities.UpsertPolicemanAsync(
                userId.Value, staffUserId, body.Packages ?? [], ct);
            return Results.Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> RevokePolicemanAsync(
        Guid staffUserId,
        ClaimsPrincipal principal,
        IAuthorityService authorities,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null) return Results.Unauthorized();
        try
        {
            await authorities.RevokePolicemanAsync(userId.Value, staffUserId, ct);
            return Results.Ok(new { revoked = true, staffUserId });
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

    private sealed record UpsertPoliceBody(IReadOnlyList<string>? Packages);
}
