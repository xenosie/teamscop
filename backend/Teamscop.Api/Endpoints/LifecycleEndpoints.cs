using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Teamscop.Api.Services;

namespace Teamscop.Api.Endpoints;

public static class LifecycleEndpoints
{
    public static RouteGroupBuilder MapLifecycleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/lifecycle").WithTags("Lifecycle");

        group.MapPost("/totp/enroll", EnrollTotpAsync).RequireAuthorization();
        group.MapGet("/totp/status", TotpStatusAsync).RequireAuthorization();
        group.MapPost("/uninstall/verify", VerifyUninstallAsync);
        group.MapPost("/uninstall/consume", ConsumeUninstallAsync);
        group.MapPost("/heartbeat", HeartbeatAsync).RequireAuthorization();

        return group;
    }

    private static async Task<IResult> EnrollTotpAsync(ClaimsPrincipal principal, ILifecycleService lifecycle, CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            var result = await lifecycle.EnrollTotpAsync(userId.Value, ct);
            return Results.Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    private static async Task<IResult> TotpStatusAsync(ClaimsPrincipal principal, ILifecycleService lifecycle, CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            return Results.Ok(await lifecycle.GetTotpStatusAsync(userId.Value, ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    private static async Task<IResult> VerifyUninstallAsync(
        [FromBody] UninstallVerifyBody body,
        ILifecycleService lifecycle,
        CancellationToken ct)
    {
        try
        {
            var ticket = await lifecycle.VerifyUninstallAsync(body.DeviceKey, body.TotpCode, ct);
            return Results.Ok(ticket);
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

    private static async Task<IResult> ConsumeUninstallAsync(
        [FromBody] ConsumeTicketBody body,
        ILifecycleService lifecycle,
        CancellationToken ct)
    {
        var ok = await lifecycle.ConsumeUninstallTicketAsync(body.UninstallTicket, ct);
        return ok
            ? Results.Ok(new { allowed = true })
            : Results.Json(new { allowed = false, error = "Invalid or expired uninstall ticket." },
                statusCode: StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> HeartbeatAsync(ClaimsPrincipal principal, ILifecycleService lifecycle, CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            await lifecycle.HeartbeatAsync(userId.Value, ct);
            return Results.Ok(new { ok = true, at = DateTimeOffset.UtcNow });
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

    private sealed record UninstallVerifyBody(string DeviceKey, string TotpCode);
    private sealed record ConsumeTicketBody(string UninstallTicket);
}
