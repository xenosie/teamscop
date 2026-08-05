using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Teamscop.Engine.Tracking;

public sealed class ChromeVisit
{
    public required string Profile { get; init; }
    public required string Url { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset VisitedAt { get; init; }
    public required long VisitId { get; init; }
}

/// <summary>
/// Reads newly added Chrome history across all profiles since install watermark.
/// Copies History DB first (Chrome locks the live file) — low impact, no browser hooks.
/// </summary>
public sealed class ChromeHistoryWatcher
{
    private readonly string _statePath;
    private readonly Dictionary<string, long> _watermarks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ChromeHistoryWatcher(string agentRoot)
    {
        var dir = Path.Combine(agentRoot, "chrome");
        Directory.CreateDirectory(dir);
        _statePath = Path.Combine(dir, "watermarks.json");
        Load();
    }

    public IReadOnlyList<ChromeVisit> PollNewVisits()
    {
        var results = new List<ChromeVisit>();
        foreach (var profileDir in EnumerateChromeProfileDirs())
        {
            var profileName = Path.GetFileName(profileDir);
            var historyPath = Path.Combine(profileDir, "History");
            if (!File.Exists(historyPath))
            {
                continue;
            }

            try
            {
                var visits = ReadVisits(profileName, historyPath);
                results.AddRange(visits);
            }
            catch
            {
                // Profile locked/unavailable — skip this cycle; try again later.
            }
        }

        return results;
    }

    public byte[] SerializeVisits(IReadOnlyList<ChromeVisit> visits)
        => JsonSerializer.SerializeToUtf8Bytes(new
        {
            fetchedAt = DateTimeOffset.UtcNow,
            count = visits.Count,
            visits
        });

    private IReadOnlyList<ChromeVisit> ReadVisits(string profileName, string historyPath)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"ts-chrome-{Guid.NewGuid():N}.db");
        File.Copy(historyPath, tmp, overwrite: true);
        // Chrome also uses WAL; copy if present for consistency.
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var side = historyPath + suffix;
            if (File.Exists(side))
            {
                try { File.Copy(side, tmp + suffix, overwrite: true); } catch { /* ignore */ }
            }
        }

        try
        {
            long since;
            lock (_gate)
            {
                _watermarks.TryGetValue(profileName, out since);
            }

            // Chrome timestamp: microseconds since 1601-01-01 UTC
            var list = new List<ChromeVisit>();
            var maxVisit = since;
            using var conn = new SqliteConnection($"Data Source={tmp};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT v.id, u.url, COALESCE(u.title, ''), v.visit_time
                FROM visits v
                JOIN urls u ON u.id = v.url
                WHERE v.visit_time > $since
                ORDER BY v.visit_time ASC
                LIMIT 500
                """;
            cmd.Parameters.AddWithValue("$since", since);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var visitId = reader.GetInt64(0);
                var url = reader.GetString(1);
                var title = reader.GetString(2);
                var chromeTime = reader.GetInt64(3);
                maxVisit = Math.Max(maxVisit, chromeTime);
                list.Add(new ChromeVisit
                {
                    Profile = profileName,
                    Url = url,
                    Title = title,
                    VisitId = visitId,
                    VisitedAt = ChromeTimeToDateTime(chromeTime)
                });
            }

            if (maxVisit > since)
            {
                lock (_gate)
                {
                    _watermarks[profileName] = maxVisit;
                    SaveUnsafe();
                }
            }

            return list;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                try { File.Delete(tmp + suffix); } catch { /* ignore */ }
            }
        }
    }

    private static DateTimeOffset ChromeTimeToDateTime(long chromeTime)
    {
        // chromeTime is microseconds since 1601-01-01
        var epoch = new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return epoch.AddTicks(chromeTime * 10); // 1 tick = 100ns; 1 us = 10 ticks
    }

    private static IEnumerable<string> EnumerateChromeProfileDirs()
    {
        foreach (var userData in ChromeUserDataRoots())
        {
            if (!Directory.Exists(userData))
            {
                continue;
            }

            var defaultDir = Path.Combine(userData, "Default");
            if (Directory.Exists(defaultDir))
            {
                yield return defaultDir;
            }

            foreach (var dir in Directory.EnumerateDirectories(userData, "Profile *"))
            {
                yield return dir;
            }
        }
    }

    private static IEnumerable<string> ChromeUserDataRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(local, "Google", "Chrome", "User Data");
            yield return Path.Combine(local, "Chromium", "User Data");
            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".config", "google-chrome");
        yield return Path.Combine(home, ".config", "chromium");
    }

    private void Load()
    {
        if (!File.Exists(_statePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_statePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, long>>(json);
            if (data is null)
            {
                return;
            }

            lock (_gate)
            {
                _watermarks.Clear();
                foreach (var kv in data)
                {
                    _watermarks[kv.Key] = kv.Value;
                }
            }
        }
        catch
        {
            // corrupt watermark file — start fresh (may re-send some history once)
        }
    }

    private void SaveUnsafe()
    {
        var json = JsonSerializer.Serialize(_watermarks);
        var tmp = _statePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _statePath, overwrite: true);
    }
}
