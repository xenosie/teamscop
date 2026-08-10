using System.Globalization;

namespace Teamscop.App.Composition;

public enum UiLogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

public sealed record UiLogEntry(DateTimeOffset At, UiLogLevel Level, string Message)
{
    public override string ToString()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{At.UtcDateTime:yyyy-MM-dd HH:mm:ss} {Level.ToString().ToUpperInvariant(),-5} {Message}");
}

/// <summary>
/// The app's only log: a daily rolling file plus a small in-memory ring the shell can show.
/// Cheap by construction (§15.2) — one lock, one append, no background thread. It never throws:
/// a logger that can fail the caller is worse than no logger.
/// </summary>
public sealed class UiLog
{
    private const int RingCapacity = 200;
    private const int KeepFiles = 7;

    private readonly object _gate = new();
    private readonly Queue<UiLogEntry> _ring = new(RingCapacity);
    private readonly string? _directory;
    private DateOnly _fileDay;
    private string? _filePath;

    public UiLog(string? directory = null)
    {
        _directory = directory ?? DefaultDirectory();
        try
        {
            if (_directory is not null)
            {
                Directory.CreateDirectory(_directory);
                Prune(_directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No log directory: the ring buffer still works, the app still runs.
            _directory = null;
        }
    }

    public void Debug(string message) => Write(UiLogLevel.Debug, message, null);

    public void Info(string message) => Write(UiLogLevel.Info, message, null);

    public void Warn(string message, Exception? ex = null) => Write(UiLogLevel.Warn, message, ex);

    public void Error(string message, Exception? ex = null) => Write(UiLogLevel.Error, message, ex);

    /// <summary>Newest last. A snapshot — safe to enumerate off the UI thread.</summary>
    public IReadOnlyList<UiLogEntry> Recent()
    {
        lock (_gate)
        {
            return _ring.ToArray();
        }
    }

    /// <summary>Most recent Warn/Error inside the window, or null when nothing is wrong.</summary>
    public UiLogEntry? LastProblem(TimeSpan within)
    {
        var cutoff = DateTimeOffset.UtcNow - within;
        lock (_gate)
        {
            UiLogEntry? hit = null;
            foreach (var entry in _ring)
            {
                if (entry.Level >= UiLogLevel.Warn && entry.At >= cutoff)
                {
                    hit = entry;
                }
            }

            return hit;
        }
    }

    private void Write(UiLogLevel level, string message, Exception? ex)
    {
        var text = ex is null ? message : $"{message} — {ex.GetType().Name}: {ex.Message}";
        var entry = new UiLogEntry(DateTimeOffset.UtcNow, level, text);

        lock (_gate)
        {
            _ring.Enqueue(entry);
            while (_ring.Count > RingCapacity)
            {
                _ring.Dequeue();
            }

            if (level == UiLogLevel.Debug || _directory is null)
            {
                return;
            }

            try
            {
                var today = DateOnly.FromDateTime(entry.At.UtcDateTime);
                if (_filePath is null || today != _fileDay)
                {
                    _fileDay = today;
                    _filePath = Path.Combine(
                        _directory,
                        $"app-{today.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
                    Prune(_directory);
                }

                File.AppendAllText(_filePath, entry + Environment.NewLine);
            }
            catch (Exception ioEx) when (ioEx is IOException or UnauthorizedAccessException)
            {
                // Disk full or locked file: keep the ring, stop touching the disk this call.
            }
        }
    }

    private static void Prune(string directory)
    {
        try
        {
            var files = Directory.GetFiles(directory, "app-*.log")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .Skip(KeepFiles);
            foreach (var file in files)
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Pruning is housekeeping — never worth failing a log write over.
        }
    }

    private static string? DefaultDirectory()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(local) ? null : Path.Combine(local, "Teamscop", "logs");
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}
