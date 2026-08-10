using System.Security.Claims;

namespace Teamscop.Api.Endpoints;

internal static class CallerExtensions
{
    /// <summary>
    /// The authenticated user id, or null when the token carries no usable subject. A null here
    /// is a genuine 401 — the middleware must never synthesise that from a 403.
    /// </summary>
    public static Guid? UserId(this ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
