using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Teamscop.Api.Services;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Endpoints;

public static class IngestEndpoints
{
    public static RouteGroupBuilder MapIngestEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/ingest").WithTags("Ingest");
        group.MapPost("/batch", IngestBatchAsync).RequireAuthorization();
        return group;
    }

    private static async Task<IResult> IngestBatchAsync(
        ClaimsPrincipal principal,
        [FromBody] IngestBatchRequest request,
        IIngestService ingest,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            var result = await ingest.IngestBatchAsync(userId.Value, request, ct);
            return Results.Ok(result);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
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
