using Npgsql;

namespace Teamscop.Api.Tests;

/// <summary>
/// C1 — the in-memory provider enforces no unique index and does not implement
/// <c>ExecuteDeleteAsync</c>, which is exactly the machinery the enrolment race (B7), the team
/// exclusivity invariants (B6) and the retention pruner depend on. Those tests run here instead,
/// against a scratch database built by replaying the real migration chain, so a broken migration
/// fails a test rather than a deploy. Everything else stays on the fast in-memory path.
/// </summary>
internal static class PostgresTestServer
{
    private const string EnvVar = "TEAMSCOP_TEST_PG";

    /// <summary>
    /// Set by CI (<c>.github/workflows/e2e.yml</c>). Skipping is the right default on a developer
    /// machine with no server, and exactly the wrong one on a build agent: these tests were
    /// reported green through six migrations they never executed, because a skip and a pass look
    /// identical in a summary line. Where it is set, an unreachable server fails the run.
    /// </summary>
    private const string RequireEnvVar = "TEAMSCOP_REQUIRE_PG";

    /// <summary>
    /// A role dedicated to the test suite, deliberately NOT the application's role: these tests
    /// create and drop databases, so they must never be able to reach — or have their credential
    /// confused with — the one the deployed API authenticates as. Provision it once with:
    /// <code>
    /// sudo -u postgres psql -c "CREATE ROLE teamscop_test LOGIN CREATEDB PASSWORD 'teamscop_test'"
    /// </code>
    /// or point <c>TEAMSCOP_TEST_PG</c> at any server you would rather use. Absent either, the
    /// persistence tests skip and the rest of the suite runs unaffected.
    /// </summary>
    private const string DefaultConnection =
        "Host=127.0.0.1;Port=5432;Database=postgres;Username=teamscop_test;Password=teamscop_test";

    private static readonly Lazy<string?> Probe = new(Connect, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Null when PostgreSQL answered; otherwise why the persistence tests are skipped.</summary>
    public static string? SkipReason => Probe.Value;

    public static bool Available => Probe.Value is null;

    /// <summary>True where a missing server must fail the run rather than skip it.</summary>
    public static bool Required
        => Environment.GetEnvironmentVariable(RequireEnvVar) is { Length: > 0 } value
            && !value.Equals("0", StringComparison.Ordinal)
            && !value.Equals("false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A scratch database for one fixture, or null when the fixture's tests are skipping anyway.
    /// Never null under <see cref="Required"/>: a fixture that quietly fell back to the in-memory
    /// provider would report a green run for tests whose whole purpose is to touch real SQL.
    /// </summary>
    public static string? CreateScratchDatabaseOrSkip()
    {
        if (Available)
        {
            return CreateScratchDatabase();
        }

        if (Required)
        {
            throw new InvalidOperationException(
                $"{SkipReason} {RequireEnvVar} is set, so this is a failure rather than a skip.");
        }

        return null;
    }

    /// <summary>Creates an empty database and hands back a connection string pointing at it.</summary>
    public static string CreateScratchDatabase()
    {
        var name = "teamscop_test_" + Guid.NewGuid().ToString("N");
        Execute($"CREATE DATABASE \"{name}\"");
        return new NpgsqlConnectionStringBuilder(MaintenanceConnection) { Database = name }.ConnectionString;
    }

    public static void DropScratchDatabase(string connectionString)
    {
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        Execute($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
    }

    private static string MaintenanceConnection
        => Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 } configured
            ? configured
            : DefaultConnection;

    private static string? Connect()
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(MaintenanceConnection) { Timeout = 3 };
            using var connection = new NpgsqlConnection(builder.ConnectionString);
            connection.Open();
            return null;
        }
        catch (Exception ex)
        {
            return $"PostgreSQL not reachable ({ex.GetType().Name}). Set {EnvVar} to run persistence tests.";
        }
    }

    private static void Execute(string sql)
    {
        using var connection = new NpgsqlConnection(MaintenanceConnection);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips itself when no PostgreSQL is reachable — unless
/// <c>TEAMSCOP_REQUIRE_PG</c> says the environment promised one, in which case it runs and fails
/// on the connection, because a skipped schema test is indistinguishable from an absent one.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!PostgresTestServer.Available && !PostgresTestServer.Required)
        {
            Skip = PostgresTestServer.SkipReason;
        }
    }
}
