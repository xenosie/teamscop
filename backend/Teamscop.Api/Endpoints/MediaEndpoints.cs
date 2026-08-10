using System.Security.Claims;
using Teamscop.Api.Services;

namespace Teamscop.Api.Endpoints;

/// <summary>
/// B12 — avatars, served to authenticated callers only.
///
/// They were static files on a public path: the GUID file name made them unguessable, but a URL
/// that leaked once stayed readable by anyone, forever, with no token. §14.6 makes this desktop
/// only and the app already carries a bearer token on every other request, so nothing is lost by
/// requiring one here.
/// </summary>
public static class MediaEndpoints
{
    public static IEndpointRouteBuilder MapMediaEndpoints(this WebApplication app)
    {
        var media = app.MapGroup("/api/media").WithTags("Media").RequireRateLimiting("api");
        media.MapGet("/avatars/{fileName}", GetAvatarAsync).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> GetAvatarAsync(
        string fileName,
        HttpContext http,
        ClaimsPrincipal principal,
        IAvatarAccess avatars,
        CancellationToken ct)
    {
        var viewerId = principal.UserId();
        if (viewerId is null)
        {
            return Results.Unauthorized();
        }

        var avatar = await avatars.ResolveAsync(viewerId.Value, fileName, ct);
        if (avatar is null)
        {
            // Not found, whether the file is missing or merely none of the caller's business:
            // a distinct 403 would confirm which avatars exist.
            return Results.NotFound();
        }

        http.Response.Headers.XContentTypeOptions = "nosniff";
        // Private: this is one employee's face, and proxies are shared.
        http.Response.Headers.CacheControl = "private, max-age=86400";
        return Results.File(avatar.Path, avatar.ContentType);
    }
}
