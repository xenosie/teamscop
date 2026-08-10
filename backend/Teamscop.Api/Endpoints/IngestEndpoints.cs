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
        var userId = principal.UserId();
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(await ingest.IngestBatchAsync(userId.Value, request, ct));
    }
}
