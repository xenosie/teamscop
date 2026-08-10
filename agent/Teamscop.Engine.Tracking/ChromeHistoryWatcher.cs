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

    // §4.2 — the registrable-domain rollup is derived ON THE SERVER from the full URL
    // (BrowsingQueryService), which deliberately ignores any payload domain. So the agent no longer
    // computes or sends one: the full URL is the only browsing fact on the wire.
}

/// <summary>
/// Reads newly added Chrome history across all profiles since install watermark.
/// Copies History DB first (Chrome locks the live file) — low impact, no browser hooks.
/// </summary>
public sealed class ChromeHistoryWatcher
{
    private readonly string _scratchDir;
    private readonly string _statePath;
    private readonly string _cutoffPath;
    private readonly long _installCutoffChromeTime;
    private readonly Func<IEnumerable<string>> _profileDirs;
    private readonly Dictionary<string, long> _watermarks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <param name="installUtc">
    /// The moment the agent was installed on this machine. Seeds the write-once §4.1 cutoff on first
    /// run; ignored on every later run because the persisted cutoff never moves. Null defaults to
    /// "now at first construction", which is exactly the "watermark on first run" the spec asks for.
    /// </param>
    /// <param name="profileDirs">Test seam. Null uses the real Chrome user-data locations.</param>
    public ChromeHistoryWatcher(
        string agentRoot,
        DateTimeOffset? installUtc = null,
        Func<IEnumerable<string>>? profileDirs = null)
    {
        var dir = Path.Combine(agentRoot, "chrome");
        Directory.CreateDirectory(dir);
        _scratchDir = Path.Combine(agentRoot, "scratch");
        Directory.CreateDirectory(_scratchDir);
        _statePath = Path.Combine(dir, "watermarks.json");
        _cutoffPath = Path.Combine(dir, "install-cutoff.json");
        _profileDirs = profileDirs ?? EnumerateChromeProfileDirs;
        _installCutoffChromeTime = LoadOrSeedInstallCutoff(installUtc ?? DateTimeOffset.UtcNow);
        SweepStaleScratchFiles();
        Load();
    }

    public IReadOnlyList<ChromeVisit> PollNewVisits()
    {
        var results = new List<ChromeVisit>();
        foreach (var profileDir in _profileDirs())
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
        long since;
        lock (_gate)
        {
            _watermarks.TryGetValue(profileName, out since);
        }

        // §4.1 — floor every profile at the install cutoff. A profile with no stored watermark (the
        // first poll ever, OR a profile created after install) therefore starts at the cutoff instead
        // of 0, so `WHERE v.visit_time > $since` can never surface a visit from before the agent was
        // installed. A corrupt watermarks.json ("start fresh") is bounded the same way.
        since = Math.Max(since, _installCutoffChromeTime);

        if (since > 0 && File.Exists(_statePath))
        {
            // WAL writes often do not bump History mtime until checkpoint — also watch -wal.
            var sourceWrite = File.GetLastWriteTimeUtc(historyPath);
            var walPath = historyPath + "-wal";
            if (File.Exists(walPath))
            {
                var walWrite = File.GetLastWriteTimeUtc(walPath);
                if (walWrite > sourceWrite)
                {
                    sourceWrite = walWrite;
                }
            }

            var watermarkWrite = File.GetLastWriteTimeUtc(_statePath);
            if (sourceWrite <= watermarkWrite)
            {
                return [];
            }
        }

        // PID-scoped scratch avoids StaffService + SessionHelper colliding on the same path.
        var tmp = Path.Combine(_scratchDir, $"chrome-{SanitizeProfileName(profileName)}-{Environment.ProcessId}.db");
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
            // Chrome timestamp: microseconds since 1601-01-01 UTC
            var list = new List<ChromeVisit>();
            var maxVisit = since;
            using var conn = new SqliteConnection($"Data Source={tmp};Mode=ReadOnly;Pooling=False");
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
            SqliteConnection.ClearAllPools();
            try { File.Delete(tmp); } catch { /* ignore */ }
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                try { File.Delete(tmp + suffix); } catch { /* ignore */ }
            }
        }
    }

    private static string SanitizeProfileName(string profileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[profileName.Length];
        for (var i = 0; i < profileName.Length; i++)
        {
            buffer[i] = invalid.Contains(profileName[i]) ? '_' : profileName[i];
        }

        return new string(buffer);
    }

    private void SweepStaleScratchFiles()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), "ts-chrome-*.db*"))
            {
                try { File.Delete(file); } catch { /* ignore */ }
            }
        }
        catch
        {
            // temp dir unavailable — skip
        }

        try
        {
            var pidMarker = $"-{Environment.ProcessId}.";
            foreach (var file in Directory.EnumerateFiles(_scratchDir, "chrome-*.db*"))
            {
                try
                {
                    var name = Path.GetFileName(file);
                    var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(file);
                    // Own PID leftovers, legacy non-pid names, or anything older than 1h.
                    var own = name.Contains(pidMarker, StringComparison.Ordinal);
                    var legacy = !name.Contains($"-{Environment.ProcessId}", StringComparison.Ordinal)
                                 && !System.Text.RegularExpressions.Regex.IsMatch(name, @"-\d+\.db");
                    if (own || legacy || age > TimeSpan.FromHours(1))
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    /* ignore */
                }
            }
        }
        catch
        {
            // scratch dir unavailable — skip
        }
    }

    private static readonly DateTimeOffset ChromeEpoch = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset ChromeTimeToDateTime(long chromeTime)
        => ChromeEpoch.AddTicks(chromeTime * 10); // 1 tick = 100ns; 1 us = 10 ticks

    /// <summary>Inverse of <see cref="ChromeTimeToDateTime"/>: a real instant → Chrome's microseconds since 1601.</summary>
    private static long UtcToChromeTime(DateTimeOffset utc)
        => (utc.UtcTicks - ChromeEpoch.UtcTicks) / 10;

    /// <summary>
    /// §4.1 — the install cutoff, written exactly once and never moved. The first construction on a
    /// machine seeds it from the install time (or now); every later construction reads it back, so a
    /// restart keeps excluding pre-install history. A missing/corrupt file re-seeds from now — the
    /// safe direction: it can only ever exclude MORE, never expose earlier history.
    /// </summary>
    private long LoadOrSeedInstallCutoff(DateTimeOffset installUtc)
    {
        try
        {
            if (File.Exists(_cutoffPath))
            {
                var existing = JsonSerializer.Deserialize<InstallCutoff>(File.ReadAllText(_cutoffPath));
                if (existing is { CutoffChromeTime: > 0 })
                {
                    return existing.CutoffChromeTime;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall through and re-seed from now — never read history before this point.
        }

        var cutoff = UtcToChromeTime(installUtc);
        try
        {
            var json = JsonSerializer.Serialize(new InstallCutoff
            {
                CutoffChromeTime = cutoff,
                SetAtUtc = installUtc
            });
            var tmp = _cutoffPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _cutoffPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // If we cannot persist it, the in-memory value still floors THIS run correctly; the next
            // run re-seeds from its own "now", which is still after install.
        }

        return cutoff;
    }

    private sealed class InstallCutoff
    {
        public long CutoffChromeTime { get; set; }
        public DateTimeOffset SetAtUtc { get; set; }
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
