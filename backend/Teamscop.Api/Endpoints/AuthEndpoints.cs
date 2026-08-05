using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Teamscop.Api.Services;

namespace Teamscop.Api.Endpoints;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/admin/signup", AdminSignupAsync).DisableAntiforgery();
        group.MapPost("/staff/signup", StaffSignupAsync).DisableAntiforgery();
        group.MapPost("/login", LoginAsync);
        group.MapGet("/me", MeAsync).RequireAuthorization();
        group.MapPost("/company-token/reveal", RevealCompanyTokenAsync).RequireAuthorization();

        return group;
    }

    private static async Task<IResult> AdminSignupAsync(
        [FromForm] string deviceKey,
        [FromForm] string username,
        [FromForm] string password,
        IFormFile? avatar,
        IAuthService auth,
        CancellationToken ct)
    {
        try
        {
            var session = await auth.AdminSignupAsync(deviceKey, username, password, avatar, ct);
            return Results.Ok(session);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> StaffSignupAsync(
        [FromForm] string deviceKey,
        [FromForm] string username,
        [FromForm] string password,
        [FromForm] string companyToken,
        IFormFile? avatar,
        IAuthService auth,
        CancellationToken ct)
    {
        try
        {
            var session = await auth.StaffSignupAsync(deviceKey, username, password, companyToken, avatar, ct);
            return Results.Ok(session);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> LoginAsync(
        [FromBody] LoginBody body,
        IAuthService auth,
        CancellationToken ct)
    {
        try
        {
            var session = await auth.LoginAsync(body.DeviceKey, body.Password, ct);
            return Results.Ok(session);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> MeAsync(ClaimsPrincipal principal, IAuthService auth, CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var me = await auth.GetMeAsync(userId.Value, ct);
        return me is null ? Results.Unauthorized() : Results.Ok(me);
    }

    private static async Task<IResult> RevealCompanyTokenAsync(ClaimsPrincipal principal, IAuthService auth, CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            var token = await auth.RevealCompanyTokenAsync(userId.Value, ct);
            return Results.Ok(new { companyToken = token });
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

    private sealed record LoginBody(string DeviceKey, string Password);
}
