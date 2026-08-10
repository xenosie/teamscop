namespace Teamscop.Api.Audit;

public sealed class AuditLog(ILogger<AuditLog> logger, IHttpContextAccessor http) : IAuditLog
{
    public void Record(string action, Guid actorUserId, Guid companyId, object? subject = null)
    {
        // UseForwardedHeaders has already run, so this is the real client IP behind nginx.
        var sourceIp = http.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "-";
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["audit"] = true });
        logger.LogInformation(
            "audit {AuditAction} actor={ActorUserId} company={CompanyId} ip={SourceIp} {@Subject}",
            action, actorUserId, companyId, sourceIp, subject);
    }
}
