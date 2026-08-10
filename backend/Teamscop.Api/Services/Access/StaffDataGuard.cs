using Teamscop.Engine.Sync;

namespace Teamscop.Api.Services.Access;

public interface IStaffDataGuard
{
    Task RequireViewableAsync(Guid viewerId, Guid staffUserId, string eventType, CancellationToken ct);

    /// <param name="allowSelf">
    /// §4.5's one exception: a staff member's own timetrack feeds their sticker.
    /// </param>
    Task RequireViewableAsync(Guid viewerId, Guid staffUserId, string eventType, bool allowSelf, CancellationToken ct);
}

/// <summary>
/// The two-step check every read-side service performs: may this viewer see this staff member,
/// and may they see this kind of data. Previously five hand-written copies.
/// </summary>
public sealed class StaffDataGuard(IAccessPolicy access) : IStaffDataGuard
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        [AgentEventTypes.ScreenshotMeta] = "screenshots",
        [AgentEventTypes.BrowserHistory] = "browsing history",
        [AgentEventTypes.TimeTrack] = "timetrack",
        // Heartbeat carries the "any monitoring package" rule, which is what health views need.
        [AgentEventTypes.Heartbeat] = "tracking health"
    };

    public Task RequireViewableAsync(Guid viewerId, Guid staffUserId, string eventType, CancellationToken ct)
        => RequireViewableAsync(viewerId, staffUserId, eventType, allowSelf: false, ct);

    public async Task RequireViewableAsync(
        Guid viewerId,
        Guid staffUserId,
        string eventType,
        bool allowSelf,
        CancellationToken ct)
    {
        if (allowSelf && viewerId == staffUserId)
        {
            return;
        }

        await access.EnsureCanViewStaffAsync(viewerId, staffUserId, ct);

        var viewer = await access.GetAsync(viewerId, ct);
        // Target-aware: a leader-and-policeman's inherent timetrack must reach only the led team,
        // while their granted packages reach company-wide. The flat check could not tell the two
        // scopes apart. See ViewerContext.CanViewEventTypeFor.
        if (!viewer.CanViewEventTypeFor(eventType, staffUserId))
        {
            throw new UnauthorizedAccessException(
                $"Missing authority package for {Labels.GetValueOrDefault(eventType, eventType)}.");
        }
    }
}
