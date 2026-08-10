using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Teamscop.Api.Services.Access;

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

    private static async Task<IResult> ListPackagesAsync(
        ClaimsPrincipal principal,
        IPolicemanAdminService police,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();
        return Results.Ok(await police.ListPackageCatalogAsync(userId.Value, ct));
    }

    private static async Task<IResult> ListPolicemenAsync(
        ClaimsPrincipal principal, IPolicemanAdminService police, CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();
        return Results.Ok(await police.ListPolicemenAsync(userId.Value, ct));
    }

    private static async Task<IResult> GetMineAsync(
        ClaimsPrincipal principal, IAccessPolicy access, CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();
        return Results.Ok(await access.GetEffectiveAsync(userId.Value, ct));
    }

    private static async Task<IResult> UpsertPolicemanAsync(
        Guid staffUserId,
        [FromBody] UpsertPoliceBody body,
        ClaimsPrincipal principal,
        IPolicemanAdminService police,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();
        return Results.Ok(await police.UpsertPolicemanAsync(userId.Value, staffUserId, body.Packages ?? [], ct));
    }

    private static async Task<IResult> RevokePolicemanAsync(
        Guid staffUserId,
        ClaimsPrincipal principal,
        IPolicemanAdminService police,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();
        await police.RevokePolicemanAsync(userId.Value, staffUserId, ct);
        return Results.Ok(new { revoked = true, staffUserId });
    }

    private sealed record UpsertPoliceBody(IReadOnlyList<string>? Packages);
}
