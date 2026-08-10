using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Teamscop.Api.Services;

namespace Teamscop.Api.Endpoints;

public static class LifecycleEndpoints
{
    public static RouteGroupBuilder MapLifecycleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/lifecycle").WithTags("Lifecycle");

        // §6.1 — codes are derived on demand from the device key: no enrolment, no stored secret,
        // no provisioning route. The staff list and code endpoints are all the Codes screen needs.
        group.MapGet("/totp/staff", ListStaffTotpAsync).RequireAuthorization().RequireRateLimiting("api");
        group.MapGet("/totp/status/{staffUserId:guid}", TotpStatusAsync).RequireAuthorization().RequireRateLimiting("api");
        group.MapGet("/totp/code/{staffUserId:guid}", TotpCodeAsync).RequireAuthorization().RequireRateLimiting("api");
        group.MapPost("/uninstall/verify", VerifyUninstallAsync).RequireRateLimiting("lifecycleAnon");
        group.MapPost("/uninstall/consume", ConsumeUninstallAsync).RequireRateLimiting("lifecycleAnon");
        group.MapPost("/usb/verify", VerifyUsbAsync).RequireRateLimiting("lifecycleAnon");
        group.MapPost("/usb/consume", ConsumeUsbAsync).RequireRateLimiting("lifecycleAnon");
        group.MapPost("/heartbeat", HeartbeatAsync).RequireAuthorization().RequireRateLimiting("api");
        group.MapPost("/app-report", AppReportAsync).RequireAuthorization().RequireRateLimiting("api");

        return group;
    }

    private static async Task<IResult> ListStaffTotpAsync(
        ClaimsPrincipal principal,
        ILifecycleService lifecycle,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(await lifecycle.ListStaffTotpAsync(userId.Value, ct));
    }

    private static async Task<IResult> TotpStatusAsync(
        Guid staffUserId,
        ClaimsPrincipal principal,
        ILifecycleService lifecycle,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(await lifecycle.GetTotpStatusAsync(userId.Value, staffUserId, ct));
    }

    private static async Task<IResult> TotpCodeAsync(
        Guid staffUserId,
        string? purpose,
        ClaimsPrincipal principal,
        ILifecycleService lifecycle,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(await lifecycle.GetTotpCodeAsync(userId.Value, staffUserId, purpose, ct));
    }

    /// <summary>
    /// Keeps a local catch. A rejected approval code is an authentication failure — 401, not the
    /// middleware's 403 — and the failure also has to feed the per-source backoff counter.
    /// </summary>
    private static async Task<IResult> VerifyUninstallAsync(
        HttpContext http,
        [FromBody] UninstallVerifyBody body,
        ILifecycleService lifecycle,
        ITotpSourceBackoff sourceBackoff,
        CancellationToken ct)
    {
        var sourceKey = SourceKey(http, body.DeviceKey);
        try
        {
            sourceBackoff.EnsureAllowed(sourceKey);
            var ticket = await lifecycle.VerifyUninstallAsync(body.DeviceKey, body.TotpCode, ct);
            sourceBackoff.RecordSuccess(sourceKey);
            return Results.Ok(ticket);
        }
        catch (UnauthorizedAccessException ex)
        {
            sourceBackoff.RecordFailure(sourceKey);
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
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

    /// <summary>Same 401 + backoff carve-out as <see cref="VerifyUninstallAsync"/>.</summary>
    private static async Task<IResult> VerifyUsbAsync(
        HttpContext http,
        [FromBody] UsbVerifyBody body,
        ILifecycleService lifecycle,
        ITotpSourceBackoff sourceBackoff,
        CancellationToken ct)
    {
        var sourceKey = SourceKey(http, body.DeviceKey);
        try
        {
            sourceBackoff.EnsureAllowed(sourceKey);
            var ticket = await lifecycle.VerifyUsbAsync(body.DeviceKey, body.TotpCode, body.DeviceInstanceId, ct);
            sourceBackoff.RecordSuccess(sourceKey);
            return Results.Ok(ticket);
        }
        catch (UnauthorizedAccessException ex)
        {
            sourceBackoff.RecordFailure(sourceKey);
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    private static async Task<IResult> ConsumeUsbAsync(
        [FromBody] ConsumeUsbBody body,
        ILifecycleService lifecycle,
        CancellationToken ct)
    {
        var ok = await lifecycle.ConsumeUsbTicketAsync(body.UsbSessionTicket, ct);
        return ok
            ? Results.Ok(new { allowed = true })
            : Results.Json(new { allowed = false, error = "Invalid or expired USB session ticket." },
                statusCode: StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> HeartbeatAsync(
        ClaimsPrincipal principal,
        ILifecycleService lifecycle,
        HeartbeatBody? body,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        // The body is optional so an agent from before §14 keeps working unchanged.
        var report = body is null
            ? null
            : new AgentHeartbeatReport(body.CaptureState, body.CaptureReason, body.MissingComponents);

        await lifecycle.HeartbeatAsync(userId.Value, report, ct);
        return Results.Ok(new { ok = true, at = DateTimeOffset.UtcNow });
    }

    /// <summary>
    /// §14.2 — the desktop app's independent report, on its own route precisely so it can never be
    /// mistaken for the service's liveness. It runs as the monitored user, so nothing it says is
    /// allowed to make a machine look healthy; the server only ever uses it to explain silence.
    /// </summary>
    private static async Task<IResult> AppReportAsync(
        ClaimsPrincipal principal,
        ILifecycleService lifecycle,
        AppReportBody body,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        await lifecycle.AppReportAsync(
            userId.Value,
            new AppStatusReport(body.ServiceState, body.MissingComponents),
            ct);
        return Results.Ok(new { ok = true, at = DateTimeOffset.UtcNow });
    }

    private static string SourceKey(HttpContext http, string? deviceKey)
        => (http.Connection.RemoteIpAddress?.ToString() ?? "unknown")
           + "|"
           + (deviceKey ?? "").Trim().ToLowerInvariant();

    private sealed record HeartbeatBody(string? CaptureState, string? CaptureReason, string? MissingComponents);
    private sealed record AppReportBody(string? ServiceState, string? MissingComponents);

    private sealed record UninstallVerifyBody(string DeviceKey, string TotpCode);
    private sealed record ConsumeTicketBody(string UninstallTicket);
    private sealed record UsbVerifyBody(string DeviceKey, string TotpCode, string? DeviceInstanceId);
    private sealed record ConsumeUsbBody(string UsbSessionTicket);
}
