namespace Teamscop.Api.Errors;

/// <summary>
/// The single place where an exception becomes an HTTP status code.
///
/// Endpoint handlers therefore carry no try/catch: they resolve the caller and return the
/// service result. The only deliberate exceptions are the three credential-checking handlers
/// (login, password change, TOTP verify) where a rejected credential is a genuine 401 —
/// an authentication failure, not an authorization one — and 401 is what the agent's
/// re-auth path keys on.
/// </summary>
public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger,
    IHostEnvironment environment)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx);

            // A failure that threw nothing is a failure nothing logs. `Microsoft.AspNetCore` is
            // pinned to Warning, so a 401 from the JWT middleware never produced an application
            // log line at all — which is why eight consecutive 401s on the avatar route (defect 5)
            // were invisible here and had to be read out of nginx. Inside the try rather than
            // beside it, so a handled exception is still reported exactly once, below.
            //
            // 404 stays at Debug on purpose: a stale client polling a route that no longer exists
            // is this deployment's single largest traffic source (176 000 a day), and drowning the
            // real signal is how the log became useless in the first place.
            if (ctx.Response.StatusCode >= 400)
            {
                var level = ctx.Response.StatusCode == StatusCodes.Status404NotFound
                    ? LogLevel.Debug
                    : LogLevel.Information;
                logger.Log(level, "{Status} on {Method} {Path}",
                    ctx.Response.StatusCode, ctx.Request.Method, ctx.Request.Path);
            }
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // The viewer navigated away mid-screenshot. Routine, not an error.
            logger.LogDebug("Client aborted {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
        }
        catch (Exception ex)
        {
            var status = StatusFor(ex);

            if (status == StatusCodes.Status500InternalServerError)
            {
                logger.LogError(ex, "Unhandled exception on {Method} {Path} (trace {TraceId})",
                    ctx.Request.Method, ctx.Request.Path, ctx.TraceIdentifier);
            }
            else
            {
                logger.LogInformation("{Status} on {Method} {Path}: {Reason}",
                    status, ctx.Request.Method, ctx.Request.Path, ex.Message);
            }

            if (ctx.Response.HasStarted)
            {
                throw;
            }

            ctx.Response.Clear();
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";

            if (status == StatusCodes.Status500InternalServerError)
            {
                var message = environment.IsDevelopment() ? ex.Message : "An unexpected error occurred.";
                await ctx.Response.WriteAsJsonAsync(new { error = message, traceId = ctx.TraceIdentifier });
            }
            else
            {
                await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
            }
        }
    }

    private static int StatusFor(Exception ex) => ex switch
    {
        // The token verified but the account is gone. 401 so the client re-authenticates;
        // 403 would tell it the session is fine and it should keep retrying.
        SessionInvalidException => StatusCodes.Status401Unauthorized,

        // Must precede the InvalidOperationException arm — ObjectDisposedException derives from
        // it, and a disposed DbContext or stream is a scope-lifetime bug on our side, not bad
        // input. Reporting it as 400 hid the classic symptom behind a client-error status that
        // nothing alerts on and the logs treat as routine.
        ObjectDisposedException => StatusCodes.Status500InternalServerError,

        UnauthorizedAccessException => StatusCodes.Status403Forbidden,
        NotFoundException => StatusCodes.Status404NotFound,
        InvalidOperationException or ArgumentException => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status500InternalServerError
    };
}

public static class ExceptionHandlingMiddlewareExtensions
{
    public static IApplicationBuilder UseTeamscopExceptionHandling(this IApplicationBuilder app)
        => app.UseMiddleware<ExceptionHandlingMiddleware>();
}
