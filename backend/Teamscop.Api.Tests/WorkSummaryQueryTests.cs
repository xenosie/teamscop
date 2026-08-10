using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;
using Teamscop.Api.Services.Insights;

namespace Teamscop.Api.Tests;

/// <summary>
/// The flow tests run on the in-memory provider, which happily evaluates anything client-side.
/// These assert the overview / leaderboard aggregate really is one <c>GROUP BY</c> in PostgreSQL —
/// silently falling back to loading every timetrack row would be ~72 000 rows a day at 50 staff.
/// <c>ToQueryString</c> compiles the query without opening a connection.
/// </summary>
public class WorkSummaryQueryTests
{
    [Fact]
    public void SummarizeWork_TranslatesToASingleGroupedAggregate()
    {
        using var db = NewPostgresContext();
        var now = DateTimeOffset.UtcNow;

        var sql = db.AgentEvents
            .SummarizeWork([Guid.NewGuid(), Guid.NewGuid()], now.AddDays(-7), now)
            .ToQueryString();

        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sum(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"WorkedSeconds\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"IdleSeconds\"", sql, StringComparison.Ordinal);
        // Everything happens server-side: no row of agent_events is materialised.
        Assert.DoesNotContain("\"PayloadJson\"", sql, StringComparison.Ordinal);
    }

    private static AppDbContext NewPostgresContext()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=teamscop_query_shape;Username=none;Password=none")
            .Options);
}
