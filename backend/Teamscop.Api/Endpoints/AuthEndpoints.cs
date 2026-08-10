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
        group.MapPost("/password/change", ChangePasswordAsync).RequireAuthorization();

        return group;
    }

    private static async Task<IResult> AdminSignupAsync(
        [FromForm] string deviceKey,
        [FromForm] string username,
        [FromForm] string password,
        IFormFile? avatar,
        IAuthService auth,
        CancellationToken ct)
        => Results.Ok(await auth.AdminSignupAsync(deviceKey, username, password, avatar, ct));

    private static async Task<IResult> StaffSignupAsync(
        [FromForm] string deviceKey,
        [FromForm] string username,
        [FromForm] string password,
        [FromForm] string companyToken,
        IFormFile? avatar,
        IAuthService auth,
        CancellationToken ct)
        => Results.Ok(await auth.StaffSignupAsync(deviceKey, username, password, companyToken, avatar, ct));

    /// <summary>
    /// Keeps a local catch: a rejected password is an authentication failure (401), which the
    /// middleware's 403 would misreport and the desktop login screen would misread.
    /// </summary>
    private static async Task<IResult> LoginAsync(
        [FromBody] LoginBody body,
        IAuthService auth,
        CancellationToken ct)
    {
        try
        {
            return Results.Ok(await auth.LoginAsync(body.DeviceKey, body.Password, ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    private static async Task<IResult> MeAsync(ClaimsPrincipal principal, IAuthService auth, CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var me = await auth.GetMeAsync(userId.Value, ct);
        return me is null ? Results.Unauthorized() : Results.Ok(me);
    }

    private static async Task<IResult> RevealCompanyTokenAsync(
        ClaimsPrincipal principal,
        IAuthService auth,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(new { companyToken = await auth.RevealCompanyTokenAsync(userId.Value, ct) });
    }

    /// <summary>Same 401 carve-out as <see cref="LoginAsync"/>: the current password is a credential.</summary>
    private static async Task<IResult> ChangePasswordAsync(
        ClaimsPrincipal principal,
        [FromBody] ChangePasswordBody body,
        IAuthService auth,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            await auth.ChangePasswordAsync(userId.Value, body.CurrentPassword, body.NewPassword, ct);
            return Results.Ok(new { ok = true });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    private sealed record LoginBody(string DeviceKey, string Password);
    private sealed record ChangePasswordBody(string CurrentPassword, string NewPassword);
}
