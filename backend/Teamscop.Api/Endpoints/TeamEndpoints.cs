using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Teamscop.Api.Services;

namespace Teamscop.Api.Endpoints;

public static class TeamEndpoints
{
    public static RouteGroupBuilder MapTeamEndpoints(this WebApplication app)
    {
        var org = app.MapGroup("/api/org").WithTags("Org");
        org.MapGet("/structure", GetStructureAsync).RequireAuthorization();
        org.MapGet("/me", GetMyPlacementAsync).RequireAuthorization();

        var teams = app.MapGroup("/api/teams").WithTags("Teams");
        teams.MapPost("/", CreateTeamAsync).RequireAuthorization();
        teams.MapPut("/{teamId:guid}", UpdateTeamAsync).RequireAuthorization();
        teams.MapDelete("/{teamId:guid}", DeleteTeamAsync).RequireAuthorization();
        teams.MapPut("/{teamId:guid}/members", SetMembersAsync).RequireAuthorization();
        teams.MapPost("/{teamId:guid}/members", AddMemberAsync).RequireAuthorization();
        teams.MapDelete("/{teamId:guid}/members/{staffUserId:guid}", RemoveMemberAsync).RequireAuthorization();

        return teams;
    }

    private static async Task<IResult> GetStructureAsync(
        ClaimsPrincipal principal, ITeamService teams, CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null) return Results.Unauthorized();
        try
        {
            return Results.Ok(await teams.GetCompanyStructureAsync(userId.Value, ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    private static async Task<IResult> GetMyPlacementAsync(
        ClaimsPrincipal principal, ITeamService teams, CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null) return Results.Unauthorized();
        try
        {
            return Results.Ok(await teams.GetMyPlacementAsync(userId.Value, ct));
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
    }

    private static async Task<IResult> CreateTeamAsync(
        [FromBody] CreateTeamRequest body,
        ClaimsPrincipal principal,
        ITeamService teams,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null) return Results.Unauthorized();
        try
        {
            return Results.Ok(await teams.CreateTeamAsync(userId.Value, body, ct));
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

    private static async Task<IResult> UpdateTeamAsync(
        Guid teamId,
        [FromBody] UpdateTeamRequest body,
        ClaimsPrincipal principal,
        ITeamService teams,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null) return Results.Unauthorized();
        try
        {
            return Results.Ok(await teams.UpdateTeamAsync(userId.Value, teamId, body, ct));
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

    private static async Task<IResult> DeleteTeamAsync(
        Guid teamId,
        ClaimsPrincipal principal,
        ITeamService teams,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null) return Results.Unauthorized();
        try
        {
            await teams.DeleteTeamAsync(userId.Value, teamId, ct);
            return Results.Ok(new { deleted = true, teamId });
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

    private static async Task<IResult> SetMembersAsync(
        Guid teamId,
        [FromBody] SetMembersRequest body,
        ClaimsPrincipal principal,
        ITeamService teams,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null) return Results.Unauthorized();
        try
        {
            return Results.Ok(await teams.SetMembersAsync(userId.Value, teamId, body, ct));
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

    private static async Task<IResult> AddMemberAsync(
        Guid teamId,
        [FromBody] AddMemberRequest body,
        ClaimsPrincipal principal,
        ITeamService teams,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null) return Results.Unauthorized();
        try
        {
            return Results.Ok(await teams.AddMemberAsync(userId.Value, teamId, body.StaffUserId, ct));
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

    private static async Task<IResult> RemoveMemberAsync(
        Guid teamId,
        Guid staffUserId,
        ClaimsPrincipal principal,
        ITeamService teams,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null) return Results.Unauthorized();
        try
        {
            return Results.Ok(await teams.RemoveMemberAsync(userId.Value, teamId, staffUserId, ct));
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
