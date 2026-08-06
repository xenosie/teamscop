using System.Security.Cryptography;
using System.Text;

namespace Teamscop.Engine.Sync;

/// <summary>
/// PHASE9 install-dir integrity: required binaries under the staff install root.
/// Emits <see cref="AgentEventTypes.AppBroken"/> once per distinct incident (debounced).
/// </summary>
public sealed class AppBrokenWatchdog
{
    private readonly string _installRoot;
    private readonly IReadOnlyList<string> _requiredRelativePaths;
    private readonly IOutboxQueue _outbox;
    private readonly Dictionary<string, string> _baselineHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private string? _lastEmittedFingerprint;

    public AppBrokenWatchdog(
        string installRoot,
        IOutboxQueue outbox,
        IEnumerable<string>? requiredRelativePaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        _installRoot = Path.GetFullPath(installRoot);
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _requiredRelativePaths = (requiredRelativePaths ?? DefaultRequiredRelativePaths())
            .Select(NormalizeRelative)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        CaptureBaselineForExisting();
    }

    public string InstallRoot => _installRoot;

    public IReadOnlyList<string> RequiredRelativePaths => _requiredRelativePaths;

    /// <summary>Last emitted incident fingerprint (for tests).</summary>
    public string? LastEmittedFingerprint
    {
        get
        {
            lock (_gate)
            {
                return _lastEmittedFingerprint;
            }
        }
    }

    public static IReadOnlyList<string> DefaultRequiredRelativePaths()
    {
        // Prefer platform-native host name; both checked at tick time via aliases.
        if (OperatingSystem.IsWindows())
        {
            return
            [
                "Teamscop.StaffService.exe",
                "Teamscop.SessionHelper.exe",
                "Teamscop.Engine.Auth.dll",
                "Teamscop.Engine.Lifecycle.dll",
                "Teamscop.Engine.Sync.dll",
                "Teamscop.Engine.Tracking.dll",
                "Teamscop.Engine.Usb.dll"
            ];
        }

        return
        [
            "Teamscop.StaffService.dll",
            "Teamscop.SessionHelper.dll",
            "Teamscop.Engine.Auth.dll",
            "Teamscop.Engine.Lifecycle.dll",
            "Teamscop.Engine.Sync.dll",
            "Teamscop.Engine.Tracking.dll",
            "Teamscop.Engine.Usb.dll"
        ];
    }

    /// <summary>
    /// Scan install root. Returns true if an <c>app_broken</c> event was enqueued this tick.
    /// </summary>
    /// <param name="businessLocal">Optional company business-local stamp (PHASE5).</param>
    public async Task<bool> TickAsync(
        CancellationToken cancellationToken = default,
        string? businessLocal = null,
        string? businessTimeZoneId = null,
        long? businessClockVersion = null,
        bool? businessSynchronized = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var report = Inspect();
        if (report.IsHealthy)
        {
            lock (_gate)
            {
                _lastEmittedFingerprint = null;
            }

            CaptureBaselineForExisting();
            return false;
        }

        var fingerprint = report.Fingerprint;
        lock (_gate)
        {
            if (string.Equals(_lastEmittedFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return false;
            }

            _lastEmittedFingerprint = fingerprint;
        }

        object payload = businessLocal is null
            ? new
            {
                kind = AgentEventTypes.AppBroken,
                missing = report.Missing,
                altered = report.Altered,
                installRoot = _installRoot
            }
            : new
            {
                kind = AgentEventTypes.AppBroken,
                missing = report.Missing,
                altered = report.Altered,
                installRoot = _installRoot,
                businessLocal,
                businessTimeZoneId,
                businessClockVersion,
                businessSynchronized
            };

        await _outbox.EnqueueAsync(
            OutboxItem.Create(AgentEventTypes.AppBroken, payload),
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>Synchronous inspect without enqueue (tests / diagnostics).</summary>
    public IntegrityReport Inspect()
    {
        var missing = new List<string>();
        var altered = new List<string>();

        foreach (var relative in _requiredRelativePaths)
        {
            var path = ResolveExistingPath(relative);
            if (path is null)
            {
                missing.Add(relative);
                continue;
            }

            string hash;
            try
            {
                hash = Sha256Hex(path);
            }
            catch
            {
                altered.Add(relative);
                continue;
            }

            lock (_gate)
            {
                if (_baselineHashes.TryGetValue(relative, out var expected)
                    && !string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase))
                {
                    altered.Add(relative);
                }
            }
        }

        return new IntegrityReport(missing, altered);
    }

    private void CaptureBaselineForExisting()
    {
        foreach (var relative in _requiredRelativePaths)
        {
            var path = ResolveExistingPath(relative);
            if (path is null)
            {
                continue;
            }

            try
            {
                var hash = Sha256Hex(path);
                lock (_gate)
                {
                    _baselineHashes[relative] = hash;
                }
            }
            catch
            {
                // ignore unreadable
            }
        }
    }

    private string? ResolveExistingPath(string relative)
    {
        var primary = Path.Combine(_installRoot, relative);
        if (File.Exists(primary))
        {
            return primary;
        }

        // Allow .exe ↔ extensionless / .dll host naming differences across publish modes.
        foreach (var alt in Alternates(relative))
        {
            var candidate = Path.Combine(_installRoot, alt);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> Alternates(string relative)
    {
        if (relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            yield return relative[..^4];
            yield return Path.ChangeExtension(relative, ".dll");
        }
        else if (relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                 && relative.Contains("StaffService", StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.ChangeExtension(relative, ".exe");
            yield return relative[..^4];
        }
    }

    private static string NormalizeRelative(string path)
    {
        var trimmed = path.Replace('\\', '/').Trim().TrimStart('/');
        return trimmed.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string Sha256Hex(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }

    public sealed record IntegrityReport(IReadOnlyList<string> Missing, IReadOnlyList<string> Altered)
    {
        public bool IsHealthy => Missing.Count == 0 && Altered.Count == 0;

        public string Fingerprint
        {
            get
            {
                var parts = Missing.Select(m => "m:" + m)
                    .Concat(Altered.Select(a => "a:" + a))
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
                return string.Join('|', parts);
            }
        }
    }
}
