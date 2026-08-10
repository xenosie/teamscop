using System.Net;
using Microsoft.AspNetCore.Mvc;
using Teamscop.Api.Services.Export;

namespace Teamscop.Api.Endpoints;

/// <summary>
/// §ExportAPI — the read-only export surface at <c>/api/v2</c>, for exactly one external consumer.
///
/// Isolated from the product on purpose. It has its own route prefix, its own credential type, its
/// own rate-limit policy and its own query service; it shares no authentication with the JWT paths
/// the agents and the desktop app use. Nothing here writes tracking data, and nothing here can
/// widen what the product itself permits — so this API cannot break or weaken the running system.
///
/// Every endpoint authenticates on entry. There is no <c>[Authorize]</c> attribute doing it
/// invisibly: a machine credential is not an ASP.NET principal, and pretending otherwise would put
/// the export path inside the same authorization machinery the product uses for people, which is
/// how export surfaces quietly acquire permissions nobody intended.
/// </summary>
public static class ExportEndpoints
{
    public static RouteGroupBuilder MapExportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v2").WithTags("ExportV2").RequireRateLimiting("exportApi");

        // Docs are public: an LLM or engineer integrating needs to read them before holding a key.
        // They describe the shape of the API and contain no company data.
        group.MapGet("/docs-for-llm.txt", GetDocs);

        // Credentials only — no IP check. The consumer must be able to set the allowlist from a new
        // address, or a changed IP would lock them out of the only endpoint that could fix it.
        group.MapPost("/ip-allowlist", SetIpAllowlistAsync);
        group.MapGet("/ip-allowlist", GetIpAllowlistAsync);

        // Data: credentials AND source IP must both check out.
        group.MapGet("/business-time", GetBusinessTimeAsync);
        group.MapGet("/staff", ListStaffAsync);
        group.MapGet("/team-leaders", ListLeadersAsync);
        group.MapGet("/teams/{teamId:guid}/staff", GetTeamStaffAsync);
        group.MapGet("/screenshots", ListScreenshotsAsync);
        group.MapGet("/screenshots/{eventId:guid}/image", GetScreenshotImageAsync);
        group.MapGet("/timetrack", GetTimeTrackAsync);
        group.MapGet("/browsing", GetBrowsingAsync);

        return group;
    }

    private static IResult GetDocs()
        => Results.Text(ExportApiDocs.Text, "text/plain; charset=utf-8");

    // ---- credential-only endpoints -----------------------------------------------------------

    private static async Task<IResult> SetIpAllowlistAsync(
        HttpContext http,
        [FromBody] IpAllowlistBody body,
        IApiClientAuthenticator auth,
        IApiClientAdminService admin,
        CancellationToken ct)
    {
        var result = await AuthenticateAsync(http, auth, ct);
        if (!result.Ok)
        {
            return Deny(result.Failure);
        }

        var ips = body.Ips ?? [];
        if (ips.Count == 0)
        {
            return Results.BadRequest(new { error = "Provide at least one IPv4 or IPv6 address." });
        }

        if (ips.Count > 20)
        {
            return Results.BadRequest(new { error = "At most 20 addresses." });
        }

        var invalid = ips.Where(i => !IPAddress.TryParse(i?.Trim(), out _)).ToList();
        if (invalid.Count > 0)
        {
            return Results.BadRequest(new { error = "Not valid IP addresses.", invalid });
        }

        var saved = await admin.SetAllowlistAsync(result.Caller!.ClientId, ips, ct);
        return Results.Ok(new
        {
            allowedIps = saved,
            callerIp = ClientIp(http)?.ToString(),
            note = "Data endpoints now serve only these addresses."
        });
    }

    private static async Task<IResult> GetIpAllowlistAsync(
        HttpContext http,
        IApiClientAuthenticator auth,
        CancellationToken ct)
    {
        var result = await AuthenticateAsync(http, auth, ct);
        if (!result.Ok)
        {
            return Deny(result.Failure);
        }

        return Results.Ok(new
        {
            allowedIps = ApiClientAuthenticator.ParseAllowlist(result.Caller!.AllowedIps).Select(i => i.ToString()),
            callerIp = ClientIp(http)?.ToString()
        });
    }

    // ---- data endpoints ------------------------------------------------------------------------

    private static async Task<IResult> GetBusinessTimeAsync(
        HttpContext http, IApiClientAuthenticator auth, IExportQueryService q, CancellationToken ct)
    {
        var result = await AuthenticateDataAsync(http, auth, ct);
        if (!result.Ok)
        {
            return Deny(result.Failure);
        }

        var dto = await q.GetBusinessTimeAsync(result.Caller!.CompanyId, ct);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    private static async Task<IResult> ListStaffAsync(
        HttpContext http, IApiClientAuthenticator auth, IExportQueryService q, CancellationToken ct)
    {
        var result = await AuthenticateDataAsync(http, auth, ct);
        if (!result.Ok)
        {
            return Deny(result.Failure);
        }

        return Results.Ok(new { staff = await q.ListStaffAsync(result.Caller!.CompanyId, ct) });
    }

    private static async Task<IResult> ListLeadersAsync(
        HttpContext http, IApiClientAuthenticator auth, IExportQueryService q, CancellationToken ct)
    {
        var result = await AuthenticateDataAsync(http, auth, ct);
        if (!result.Ok)
        {
            return Deny(result.Failure);
        }

        return Results.Ok(new { leaders = await q.ListLeadersAsync(result.Caller!.CompanyId, ct) });
    }

    private static async Task<IResult> GetTeamStaffAsync(
        Guid teamId, HttpContext http, IApiClientAuthenticator auth, IExportQueryService q, CancellationToken ct)
    {
        var result = await AuthenticateDataAsync(http, auth, ct);
        if (!result.Ok)
        {
            return Deny(result.Failure);
        }

        var team = await q.GetTeamAsync(result.Caller!.CompanyId, teamId, ct);
        return team is null ? Results.NotFound(new { error = "Team not found." }) : Results.Ok(team);
    }

    private static async Task<IResult> ListScreenshotsAsync(
        [FromQuery] Guid staffUserId,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] int? take,
        HttpContext http, IApiClientAuthenticator auth, IExportQueryService q, CancellationToken ct)
    {
        var result = await AuthenticateDataAsync(http, auth, ct);
        if (!result.Ok)
        {
            return Deny(result.Failure);
        }

        if (!TryPeriod(from, to, out var f, out var t, out var error))
        {
            return Results.BadRequest(new { error });
        }

        var list = await q.ListScreenshotsAsync(result.Caller!.CompanyId, staffUserId, f, t, take ?? 200, ct);
        return Results.Ok(new { staffUserId, from = f, to = t, count = list.Count, screenshots = list });
    }

    private static async Task<IResult> GetScreenshotImageAsync(
        Guid eventId,
        [FromQuery] int? display,
        HttpContext http, IApiClientAuthenticator auth, IExportQueryService q, CancellationToken ct)
    {
        var result = await AuthenticateDataAsync(http, auth, ct);
        if (!result.Ok)
        {
            return Deny(result.Failure);
        }

        var image = await q.GetScreenshotImageAsync(result.Caller!.CompanyId, eventId, display ?? 1, ct);
        return image is null
            ? Results.NotFound(new { error = "Screenshot not found." })
            : Results.File(image.Value.Bytes, image.Value.ContentType, enableRangeProcessing: false);
    }

    private static async Task<IResult> GetTimeTrackAsync(
        [FromQuery] Guid staffUserId,
        [FromQuery] string? from,
        [FromQuery] string? to,
        HttpContext http, IApiClientAuthenticator auth, IExportQueryService q, CancellationToken ct)
    {
        var result = await AuthenticateDataAsync(http, auth, ct);
        if (!result.Ok)
        {
            return Deny(result.Failure);
        }

        if (!TryPeriod(from, to, out var f, out var t, out var error))
        {
            return Results.BadRequest(new { error });
        }

        var dto = await q.GetTimeTrackAsync(result.Caller!.CompanyId, staffUserId, f, t, ct);
        return dto is null ? Results.NotFound(new { error = "Staff member not found." }) : Results.Ok(dto);
    }

    private static async Task<IResult> GetBrowsingAsync(
        [FromQuery] Guid staffUserId,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] int? take,
        HttpContext http, IApiClientAuthenticator auth, IExportQueryService q, CancellationToken ct)
    {
        var result = await AuthenticateDataAsync(http, auth, ct);
        if (!result.Ok)
        {
            return Deny(result.Failure);
        }

        if (!TryPeriod(from, to, out var f, out var t, out var error))
        {
            return Results.BadRequest(new { error });
        }

        var dto = await q.GetBrowsingAsync(result.Caller!.CompanyId, staffUserId, f, t, take ?? 200, ct);
        return dto is null ? Results.NotFound(new { error = "Staff member not found." }) : Results.Ok(dto);
    }

    // ---- shared --------------------------------------------------------------------------------

    private static Task<ApiAuthResult> AuthenticateAsync(
        HttpContext http, IApiClientAuthenticator auth, CancellationToken ct)
        => auth.AuthenticateAsync(Header(http, "X-Api-Key"), Header(http, "X-Api-Secret"), ct);

    private static Task<ApiAuthResult> AuthenticateDataAsync(
        HttpContext http, IApiClientAuthenticator auth, CancellationToken ct)
        => auth.AuthenticateForDataAsync(
            Header(http, "X-Api-Key"), Header(http, "X-Api-Secret"), ClientIp(http), ct);

    private static string? Header(HttpContext http, string name)
        => http.Request.Headers.TryGetValue(name, out var v) ? v.ToString() : null;

    /// <summary>
    /// The caller's address as ASP.NET resolved it. ForwardedHeaders is configured to trust only
    /// the loopback nginx hop, so this is the real client IP and NOT something a caller can spoof
    /// by sending their own X-Forwarded-For.
    /// </summary>
    private static IPAddress? ClientIp(HttpContext http) => http.Connection.RemoteIpAddress;

    /// <summary>
    /// Refusals are deliberately coarse: 401 for anything credential-related, 403 for a good
    /// credential from the wrong place. A caller learns whether their key works, never whether a
    /// key id exists — and "unknown key" and "wrong secret" are indistinguishable.
    /// </summary>
    private static IResult Deny(ApiAuthFailure failure) => failure switch
    {
        ApiAuthFailure.IpNotAllowed => Results.Json(
            new { error = "Source IP is not on this key's allowlist." },
            statusCode: StatusCodes.Status403Forbidden),
        ApiAuthFailure.NoAllowlistConfigured => Results.Json(
            new { error = "No IP allowlist configured. POST /api/v2/ip-allowlist first." },
            statusCode: StatusCodes.Status403Forbidden),
        ApiAuthFailure.Disabled => Results.Json(
            new { error = "This API key is disabled." },
            statusCode: StatusCodes.Status403Forbidden),
        _ => Results.Json(
            new { error = "Invalid or missing X-Api-Key / X-Api-Secret." },
            statusCode: StatusCodes.Status401Unauthorized)
    };

    /// <summary>
    /// Parses and bounds a period. Both ends are required — an unbounded export is a way to ask for
    /// the entire history by accident — and the span is capped so one request cannot sweep a year.
    /// </summary>
    private static bool TryPeriod(
        string? from, string? to, out DateTimeOffset f, out DateTimeOffset t, out string? error)
    {
        f = default;
        t = default;
        error = null;

        if (!DateTimeOffset.TryParse(from, out f) || !DateTimeOffset.TryParse(to, out t))
        {
            error = "from and to are required ISO-8601 timestamps, e.g. 2026-08-10T00:00:00Z.";
            return false;
        }

        f = f.ToUniversalTime();
        t = t.ToUniversalTime();
        if (t <= f)
        {
            error = "to must be after from.";
            return false;
        }

        if (t - f > ExportQueryService.MaxPeriod)
        {
            error = $"Period must not exceed {ExportQueryService.MaxPeriod.TotalDays:0} days.";
            return false;
        }

        return true;
    }

    private sealed record IpAllowlistBody(List<string>? Ips);
}
