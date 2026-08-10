using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Teamscop.Api.Data;
using Teamscop.Api.Options;

namespace Teamscop.Api.Services;

/// <summary>Prunes old agent_events, screenshot blobs, and expired tickets.</summary>
public sealed class RetentionHostedService(
    IServiceScopeFactory scopes,
    IOptions<RetentionOptions> options,
    ILogger<RetentionHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger startup so migrate finishes first.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Retention job failed");
            }

            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One batch is bounded so a single statement never locks the table for long; the run repeats
    /// until a batch comes back short. At 50 staff the inflow is ~172 000 rows a day (§15.1), so a
    /// single 5 000-row pass every six hours could never keep up and <c>agent_events</c> grew
    /// without bound at any retention setting.
    /// </summary>
    private const int BatchSize = 5000;

    private const int MaxRowsPerRun = 200_000;

    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>
    /// Public so a test can drive one pass directly: every statement here is
    /// <c>ExecuteDeleteAsync</c>, which the in-memory provider does not implement, so this is only
    /// ever exercised against real PostgreSQL.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var days = options.Value.AgentEventsDays;
        if (days <= 0)
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blobs = scope.ServiceProvider.GetRequiredService<IScreenshotBlobStorage>();

        var pruned = 0;
        while (pruned < MaxRowsPerRun && !ct.IsCancellationRequested)
        {
            var oldIds = await db.AgentEvents.AsNoTracking()
                .Where(e => e.OccurredAt < cutoff)
                .Select(e => new { e.Id, e.UserId })
                .Take(BatchSize)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            if (oldIds.Count == 0)
            {
                break;
            }

            var idSet = oldIds.Select(x => x.Id).ToList();
            pruned += await db.AgentEvents.Where(e => idSet.Contains(e.Id)).ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
            foreach (var row in oldIds)
            {
                blobs.DeleteEvent(row.UserId, row.Id);
            }

            if (oldIds.Count < BatchSize)
            {
                break;
            }
        }

        if (pruned > 0)
        {
            logger.LogInformation("Retention pruned {Count} agent_events older than {Cutoff}", pruned, cutoff);
        }

        var tickets = await db.UninstallTickets
            .Where(t => t.ExpiresAt < DateTimeOffset.UtcNow.AddDays(-7))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        var usb = await db.UsbSessionTickets
            .Where(t => t.ExpiresAt < DateTimeOffset.UtcNow.AddDays(-7))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        if (tickets + usb > 0)
        {
            logger.LogInformation("Retention GC tickets uninstall={Uninstall} usb={Usb}", tickets, usb);
        }
    }
}
