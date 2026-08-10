using Microsoft.Data.Sqlite;
using Teamscop.Engine.Tracking;

namespace Teamscop.Api.Tests;

/// <summary>
/// §4.1 — "never read history from before the agent was installed." The watcher persists an
/// install-time cutoff on first run and floors every profile's first read at it, so no visit older
/// than the install can ever be ingested — including on the very first poll and across restarts.
///
/// The Chrome user-data location is a fixed OS path, so the watcher takes a profile-dir seam for
/// tests; the sqlite here is Chrome-shaped (urls + visits, visit_time in microseconds since 1601).
/// </summary>
public class ChromeWatermarkTests
{
    private static readonly DateTimeOffset ChromeEpoch = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static long ChromeTime(DateTimeOffset at) => (at.UtcTicks - ChromeEpoch.UtcTicks) / 10;

    [Fact]
    public void TheFirstPollEverExcludesEverythingBeforeInstall()
    {
        using var root = new AgentTestRoot("chrome");
        var install = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var profile = NewProfile(root, "Default", new[]
        {
            ("https://pre-install.example/1", install.AddDays(-3)),
            ("https://pre-install.example/2", install.AddMinutes(-1)),
            ("https://post-install.example/1", install.AddMinutes(5)),
            ("https://post-install.example/2", install.AddHours(2)),
        });

        var watcher = new ChromeHistoryWatcher(root.Path, install, () => new[] { profile });
        var visits = watcher.PollNewVisits();

        // Only post-install history — the first poll must not surface the two pre-install rows.
        Assert.Equal(2, visits.Count);
        Assert.All(visits, v => Assert.StartsWith("https://post-install.example/", v.Url));
    }

    [Fact]
    public void PreInstallHistoryStaysExcluded_EvenIfTheWatermarkFileIsLost_AcrossARestart()
    {
        using var root = new AgentTestRoot("chrome");
        var install = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var profile = NewProfile(root, "Default", new[]
        {
            ("https://pre-install.example/1", install.AddDays(-10)),
            ("https://post-install.example/1", install.AddMinutes(30)),
        });

        // First run advances the watermark past the post-install visit and writes install-cutoff.json.
        new ChromeHistoryWatcher(root.Path, install, () => new[] { profile }).PollNewVisits();

        // Simulate corruption / "start fresh": the watermark is gone, but the cutoff is not.
        File.Delete(Path.Combine(root.Path, "chrome", "watermarks.json"));

        // A restart re-seeds nothing — the persisted cutoff (NOT "now") is loaded — so the ancient
        // row is still excluded even though the per-profile watermark was lost.
        var restarted = new ChromeHistoryWatcher(root.Path, installUtc: null, () => new[] { profile });
        var visits = restarted.PollNewVisits();

        Assert.DoesNotContain(visits, v => v.Url.StartsWith("https://pre-install.example/", StringComparison.Ordinal));
    }

    [Fact]
    public void AProfileFirstSeenAfterInstall_ReturnsAllOfItsVisits()
    {
        using var root = new AgentTestRoot("chrome");
        var install = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

        // Seed the cutoff with an existing (empty-ish) profile first.
        var existing = NewProfile(root, "Default", Array.Empty<(string, DateTimeOffset)>());
        var watcher = new ChromeHistoryWatcher(root.Path, install, () => Directory.Exists(NewlyCreated)
            ? new[] { existing, NewlyCreated }
            : new[] { existing });
        watcher.PollNewVisits();

        // A profile created AFTER install: every one of its visits is post-cutoff, so none is lost.
        NewlyCreated = NewProfile(root, "Profile 1", new[]
        {
            ("https://new-profile.example/a", install.AddHours(1)),
            ("https://new-profile.example/b", install.AddHours(3)),
        });

        var visits = watcher.PollNewVisits();
        Assert.Equal(2, visits.Count);
        Assert.All(visits, v => Assert.StartsWith("https://new-profile.example/", v.Url));
    }

    private string NewlyCreated = "";

    private static string NewProfile(AgentTestRoot root, string name, (string Url, DateTimeOffset At)[] visits)
    {
        var profileDir = Path.Combine(root.Path, "userdata", name);
        Directory.CreateDirectory(profileDir);
        var historyPath = Path.Combine(profileDir, "History");

        using (var conn = new SqliteConnection($"Data Source={historyPath}"))
        {
            conn.Open();
            Exec(conn, "CREATE TABLE urls (id INTEGER PRIMARY KEY, url TEXT, title TEXT);");
            Exec(conn, "CREATE TABLE visits (id INTEGER PRIMARY KEY, url INTEGER, visit_time INTEGER);");
            var id = 1;
            foreach (var (url, at) in visits)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO urls (id, url, title) VALUES ($id, $url, $title);" +
                    "INSERT INTO visits (id, url, visit_time) VALUES ($id, $id, $t);";
                cmd.Parameters.AddWithValue("$id", id++);
                cmd.Parameters.AddWithValue("$url", url);
                cmd.Parameters.AddWithValue("$title", "t");
                cmd.Parameters.AddWithValue("$t", ChromeTime(at));
                cmd.ExecuteNonQuery();
            }
        }

        SqliteConnection.ClearAllPools(); // release the file handle so the watcher can copy it
        return profileDir;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
