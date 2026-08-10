using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Teamscop.Api.Data;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Services.Insights;

/// <summary>
/// §14 — classifies every staff machine on a fixed clock and records each change as app history.
///
/// It runs on its own timer rather than when an admin happens to look, because the interesting
/// events happen when nobody is watching: a machine that breaks at 02:00 must still produce a
/// history entry timestamped 02:00, not one timestamped whenever someone next opens the roster.
///
/// It is the single writer of <c>users.AgentStatus</c>, so the badge an admin sees and the history
/// row behind it are the same fact and cannot drift apart.
/// </summary>
public sealed class StaffStatusHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<StaffStatusHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the API finish starting and migrations finish applying before the first pass.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let one bad pass kill the loop: the status feed going silent is exactly the
                // blackout this feature exists to prevent.
                logger.LogError(ex, "Staff status pass failed; will retry.");
            }

            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTimeOffset.UtcNow;
        var staff = await db.Users
            .Where(u => u.Role == UserRole.Staff)
            .ToListAsync(ct);

        var changed = 0;
        foreach (var user in staff)
        {
            var verdict = StaffAgentStatus.Classify(
                new StaffStatusSignals(
                    ServiceHeartbeatAt: user.LastHeartbeatAt,
                    AppReportAt: user.LastAppReportAt,
                    AppServiceState: user.AppServiceState,
                    AppDefectSinceAt: user.AppDefectSinceAt,
                    CaptureState: user.LastCaptureState,
                    CaptureReason: user.LastCaptureReason,
                    ComponentsMissing: !string.IsNullOrWhiteSpace(user.LastMissingComponents),
                    MissingComponents: user.LastMissingComponents,
                    UninstalledAt: user.UninstalledAt),
                now);

            if (string.Equals(user.AgentStatus, verdict.Status, StringComparison.Ordinal))
            {
                // Same state: keep the reason current but write no history. A machine that has been
                // offline all weekend should be one row, not one row per minute.
                user.AgentStatusReason = verdict.Reason;
                continue;
            }

            db.AgentEvents.Add(new AgentEvent
            {
                Id = Guid.NewGuid(),
                CompanyId = user.CompanyId,
                UserId = user.Id,
                // Deterministic, so a second API instance ticking at the same second collides on the
                // (UserId, ClientEventId) unique index and is a no-op rather than a duplicate row.
                ClientEventId = DeterministicId(user.Id, verdict.Status, now),
                EventType = AgentEventTypes.AppStatus,
                // The server is the observer here, so both timestamps are its own clock. Using an
                // agent-supplied time would let a machine backdate its own failure.
                OccurredAt = now,
                ReceivedAt = now,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    kind = AgentEventTypes.AppStatus,
                    status = verdict.Status,
                    previous = user.AgentStatus,
                    reason = verdict.Reason,
                    sinceUtc = user.AgentStatusSince
                })
            });

            user.AgentStatus = verdict.Status;
            user.AgentStatusReason = verdict.Reason;
            user.AgentStatusSince = now;
            changed++;
        }

        if (changed > 0)
        {
            logger.LogInformation("Staff status changed for {Count} machine(s)", changed);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A stable id for "this user entered this status in this minute", so the same conclusion
    /// reached twice writes one row.
    /// </summary>
    private static Guid DeterministicId(Guid userId, string status, DateTimeOffset at)
    {
        var seed = $"{userId:D}|{status}|{at:yyyyMMddHHmm}";
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(seed));
        return new Guid(hash);
    }
}
