namespace Teamscop.Api.Audit;

/// <summary>
/// One line per security-relevant operation. Deliberately not a table: §14.5 rules out exports
/// and reports, §14.6 is desktop-only, and the deployment is one host with 20–50 employees, so
/// <c>journalctl -u teamscop-api | grep audit</c> is the query tool. This interface exists so
/// that if a table is ever wanted, <c>AuditLog.cs</c> is the only file that changes.
/// </summary>
public interface IAuditLog
{
    void Record(string action, Guid actorUserId, Guid companyId, object? subject = null);
}
