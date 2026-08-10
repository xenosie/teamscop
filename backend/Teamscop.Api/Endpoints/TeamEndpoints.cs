using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Teamscop.Api.Services;

namespace Teamscop.Api.Endpoints;

public static class TeamEndpoints
{
    public static IEndpointRouteBuilder MapTeamEndpoints(this WebApplication app)
    {
        var org = app.MapGroup("/api/org").WithTags("Org").RequireRateLimiting("api");
        org.MapGet("/structure", GetStructureAsync).RequireAuthorization();
        org.MapGet("/me", GetMyPlacementAsync).RequireAuthorization();

        var teams = app.MapGroup("/api/teams").WithTags("Teams").RequireRateLimiting("api");
        teams.MapPost("/", CreateTeamAsync).RequireAuthorization();
        teams.MapPut("/{teamId:guid}", UpdateTeamAsync).RequireAuthorization();
        teams.MapDelete("/{teamId:guid}", DeleteTeamAsync).RequireAuthorization();
        teams.MapPut("/{teamId:guid}/members", SetMembersAsync).RequireAuthorization();
        teams.MapPost("/{teamId:guid}/members", AddMemberAsync).RequireAuthorization();
        teams.MapDelete("/{teamId:guid}/members/{staffUserId:guid}", RemoveMemberAsync).RequireAuthorization();

        return app;
    }

    private static async Task<IResult> GetStructureAsync(
        ClaimsPrincipal principal, ITeamService teams, CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();
        return Results.Ok(await teams.GetCompanyStructureAsync(userId.Value, ct));
    }

    private static async Task<IResult> GetMyPlacementAsync(
        ClaimsPrincipal principal, ITeamService teams, CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();
        return Results.Ok(await teams.GetMyPlacementAsync(userId.Value, ct));
    }

    private static async Task<IResult> CreateTeamAsync(
        [FromBody] CreateTeamRequest body,
        ClaimsPrincipal principal,
        ITeamService teams,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();
        return Results.Ok(await teams.CreateTeamAsync(userId.Value, body, ct));
    }

    private static async Task<IResult> UpdateTeamAsync(
        Guid teamId,
        [FromBody] UpdateTeamRequest body,
        ClaimsPrincipal principal,
        ITeamService teams,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();
        return Results.Ok(await teams.UpdateTeamAsync(userId.Value, teamId, body, ct));
    }

    private static async Task<IResult> DeleteTeamAsync(
        Guid teamId,
        ClaimsPrincipal principal,
        ITeamService teams,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();
        await teams.DeleteTeamAsync(userId.Value, teamId, ct);
        return Results.Ok(new { deleted = true, teamId });
    }

    private static async Task<IResult> SetMembersAsync(
        Guid teamId,
        [FromBody] SetMembersRequest body,
        ClaimsPrincipal principal,
        ITeamService teams,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();
        return Results.Ok(await teams.SetMembersAsync(userId.Value, teamId, body, ct));
    }

    private static async Task<IResult> AddMemberAsync(
        Guid teamId,
        [FromBody] AddMemberRequest body,
        ClaimsPrincipal principal,
        ITeamService teams,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();
        return Results.Ok(await teams.AddMemberAsync(userId.Value, teamId, body.StaffUserId, ct));
    }

    private static async Task<IResult> RemoveMemberAsync(
        Guid teamId,
        Guid staffUserId,
        ClaimsPrincipal principal,
        ITeamService teams,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();
        return Results.Ok(await teams.RemoveMemberAsync(userId.Value, teamId, staffUserId, ct));
    }
}
