using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Teamscop.Engine.Sync;
using Teamscop.Engine.Tracking;

namespace Teamscop.StaffService;

/// <summary>
/// The service side of the SessionHelper capture pipe.
///
/// Everything that arrives here is signed into the vault as authentic, so the pipe is a trust
/// boundary and is treated as one. Two things guard it:
/// <list type="bullet">
/// <item>An explicit ACL — SYSTEM and Administrators full control, INTERACTIVE read/write. Without
/// one, the pipe inherits the LocalSystem token's default DACL and every local process can connect
/// and inject telemetry, which defeats §12.1 on the ingest path rather than the storage path.</item>
/// <item>An allowlist. <c>eventType</c> must be one of three, <c>vaultKind</c> must be the one that
/// belongs to it, the payload must be base64 that decodes to JSON under a size cap, and
/// <c>occurredAtUtc</c> is clamped — a caller-chosen timestamp is a way to backdate a record the
/// service then signs.</item>
/// </list>
/// </summary>
public sealed class CapturePipeServer : IAsyncDisposable
{
    /// <summary>Two, so a listener is always waiting while the other instance serves a client.</summary>
    private const int ServerInstances = 2;

    private const int MaxPayloadBytes = 4 * 1024 * 1024;
    private const int MaxCapturesPerMinute = 60;
    private const long MaxCaptureBytesPerMinute = 32L * 1024 * 1024;

    private static readonly TimeSpan MaxBackdate = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaxPostdate = TimeSpan.FromMinutes(2);

    /// <summary>The only capture frames that exist, and the vault kind each one may write.</summary>
    private static readonly Dictionary<string, string> AllowedCaptures = new(StringComparer.OrdinalIgnoreCase)
    {
        [AgentEventTypes.TimeTrack] = "timetrack",
        [AgentEventTypes.ScreenshotMeta] = "screenshot",
        [AgentEventTypes.BrowserHistory] = "browser_history"
    };

    private readonly TrackingCoordinator _tracking;
    private readonly ILogger<CapturePipeServer> _logger;
    private readonly string _pipeName;
    private CancellationTokenSource? _cts;
    private Thread[]? _listeners;
    private long _acceptedFrames;
    private long _rejectedFrames;
    private DateTimeOffset? _lastCaptureAcceptedAtUtc;

    public CapturePipeServer(
        TrackingCoordinator tracking,
        ILogger<CapturePipeServer> logger,
        string? pipeName = null)
    {
        _tracking = tracking;
        _logger = logger;
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? SessionHelperPipeNames.Default : pipeName;
    }

    public string PipeName => _pipeName;

    /// <summary>When the helper last delivered a capture the service accepted. Feeds A15.</summary>
    public DateTimeOffset? LastCaptureAcceptedAtUtc => _lastCaptureAcceptedAtUtc;

    public long AcceptedFrames => Interlocked.Read(ref _acceptedFrames);

    public long RejectedFrames => Interlocked.Read(ref _rejectedFrames);

    public void Start()
    {
        if (_listeners is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Dedicated threads, not thread-pool work items: these block for their whole lifetime, which
        // is exactly what the pool must not be asked to do. Two of them, matching ServerInstances.
        _listeners = new Thread[ServerInstances];
        for (var i = 0; i < ServerInstances; i++)
        {
            var thread = new Thread(() => Listen(token))
            {
                IsBackground = true,
                Name = $"teamscop-capture-pipe-{i}"
            };
            _listeners[i] = thread;
            thread.Start();
        }
    }

    /// <summary>
    /// Accept loop, running blocking I/O on its own thread.
    ///
    /// This pipe used to be asynchronous, and it crash-looped the service. Overlapped pipe I/O frees
    /// its NativeOverlapped as part of unwinding a cancelled or disposed operation, but the I/O
    /// completion has already been queued and still arrives. It then dereferences the freed
    /// overlapped and the runtime raises
    /// <c>InvalidOperationException: 'overlapped' has already been freed</c> from
    /// <c>IOCompletionPoller</c> — on a thread-pool thread, where no catch anywhere in this process
    /// can intercept it. The process dies, SCM restarts it, and it dies again roughly a second later,
    /// never surviving long enough to send a heartbeat. Every accept, read and write here was a way
    /// to hit that.
    ///
    /// So the whole class is removed rather than narrowed: nothing here is overlapped, there are no
    /// completion callbacks, and cancellation never interrupts an in-flight operation. The cost is
    /// two blocked threads for a pipe that carries a ping every few seconds and a screenshot every
    /// few minutes, which is a trade worth making for a service that must not die.
    ///
    /// Shutdown works with the grain of that design: <see cref="DisposeAsync"/> sets the token and
    /// connects a throwaway client, the blocking accept returns normally, the loop sees cancellation
    /// and exits. A thread parked in a blocking read on a live connection is a background thread and
    /// is reclaimed at process exit.
    /// </summary>
    private void Listen(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServer();
                server.WaitForConnection();

                if (ct.IsCancellationRequested)
                {
                    break; // woken by the shutdown breaker, not by a real client
                }

                HandleClient(server, ct);
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                _logger.LogWarning(ex, "Capture pipe listener error on {Pipe}", _pipeName);
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            }
            finally
            {
                try { server?.Dispose(); }
                catch (Exception ex) { _logger.LogDebug(ex, "Capture pipe dispose"); }
            }
        }
    }

    private NamedPipeServerStream CreateServer()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Linux/CI: .NET implements named pipes over Unix domain sockets, whose access is
            // governed by the socket file's permissions. There is no PipeSecurity to apply.
            return new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                ServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.None);
        }

        // No PipeOptions.Asynchronous — see Listen. The helper's client may still be asynchronous;
        // the flag is per-handle, so the two interoperate normally.
        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            ServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.WriteThrough,
            inBufferSize: 64 * 1024,
            outBufferSize: 8 * 1024,
            BuildSecurity());
    }

    /// <summary>
    /// SYSTEM and Administrators may do anything; INTERACTIVE (S-1-5-4) — the logged-on user, which
    /// is the only account that can legitimately capture a desktop — may read and write. Never
    /// Everyone, never Authenticated Users: a service account or a scheduled task has no business
    /// writing to the vault.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static PipeSecurity BuildSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));
        return security;
    }

    private void HandleClient(NamedPipeServerStream server, CancellationToken ct)
    {
        var windowStartedAt = DateTimeOffset.UtcNow;
        var capturesInWindow = 0;
        var bytesInWindow = 0L;

        while (!ct.IsCancellationRequested && server.IsConnected)
        {
            var message = SessionHelperPipeCodec.Read<SessionHelperMessage>(server);
            if (message is null)
            {
                break;
            }

            if (string.Equals(message.Type, "ping", StringComparison.OrdinalIgnoreCase))
            {
                _tracking.NoteSessionHelperAlive();
                Reply(server, ConfigReply());
                continue;
            }

            if (!string.Equals(message.Type, "capture", StringComparison.OrdinalIgnoreCase))
            {
                Reject(server, "unknown_type");
                continue;
            }

            if (DateTimeOffset.UtcNow - windowStartedAt >= TimeSpan.FromMinutes(1))
            {
                windowStartedAt = DateTimeOffset.UtcNow;
                capturesInWindow = 0;
                bytesInWindow = 0;
            }

            var payload = ValidateCapture(message, out var vaultKind, out var error);
            if (payload is null)
            {
                Reject(server, error!);
                continue;
            }

            capturesInWindow++;
            bytesInWindow += payload.Length;
            if (capturesInWindow > MaxCapturesPerMinute || bytesInWindow > MaxCaptureBytesPerMinute)
            {
                Interlocked.Increment(ref _rejectedFrames);
                _logger.LogWarning(
                    "Capture pipe budget exceeded ({Frames} frames / {Bytes} bytes in one minute); dropping connection",
                    capturesInWindow,
                    bytesInWindow);
                break;
            }

            // Blocking on a dedicated thread, so there is no pool starvation and no sync context to
            // deadlock against. GetAwaiter().GetResult() also rethrows the original exception rather
            // than wrapping it, which keeps the log line honest.
            _tracking.PersistFromHelperAsync(
                    message.EventType!,
                    vaultKind!,
                    payload,
                    ClampOccurredAt(message.OccurredAtUtc),
                    ct)
                .GetAwaiter()
                .GetResult();

            Interlocked.Increment(ref _acceptedFrames);
            _lastCaptureAcceptedAtUtc = DateTimeOffset.UtcNow;
            Reply(server, new SessionHelperReply { Ok = true });
        }
    }

    /// <summary>Decoded payload when the frame is acceptable; null plus a stable reason otherwise.</summary>
    private static byte[]? ValidateCapture(SessionHelperMessage message, out string? vaultKind, out string? error)
    {
        vaultKind = null;
        error = null;

        if (string.IsNullOrWhiteSpace(message.EventType)
            || !AllowedCaptures.TryGetValue(message.EventType, out var expectedKind))
        {
            error = "event_type_not_allowed";
            return null;
        }

        if (!string.Equals(message.VaultKind, expectedKind, StringComparison.OrdinalIgnoreCase))
        {
            error = "vault_kind_mismatch";
            return null;
        }

        // Length-checked before decoding: the frame codec allows 16 MB, and refusing early keeps
        // the 4 MB cap from being a 12 MB allocation first.
        if (string.IsNullOrWhiteSpace(message.PayloadBase64)
            || message.PayloadBase64.Length > (MaxPayloadBytes / 3 * 4) + 4)
        {
            error = "bad_payload";
            return null;
        }

        var buffer = new byte[(message.PayloadBase64.Length / 4 * 3) + 3];
        if (!Convert.TryFromBase64String(message.PayloadBase64, buffer, out var written)
            || written == 0
            || written > MaxPayloadBytes)
        {
            error = "bad_payload";
            return null;
        }

        var payload = buffer.AsSpan(0, written).ToArray();
        try
        {
            using var _ = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            error = "bad_payload";
            return null;
        }

        vaultKind = expectedKind;
        return payload;
    }

    /// <summary>
    /// A hostile local process could otherwise choose the timestamp on a record the service signs.
    /// Anything outside the plausible window becomes "now" rather than being rejected — the capture
    /// itself is still real, only its claimed time is not.
    /// </summary>
    private static DateTimeOffset ClampOccurredAt(DateTimeOffset? claimed)
    {
        var now = DateTimeOffset.UtcNow;
        if (claimed is not { } when || when < now - MaxBackdate || when > now + MaxPostdate)
        {
            return now;
        }

        return when;
    }

    private SessionHelperReply ConfigReply()
    {
        var config = _tracking.Config;
        return new SessionHelperReply
        {
            Ok = true,
            ScreenshotEnabled = config.ScreenshotEnabled,
            TimeTrackEnabled = config.TimeTrackEnabled,
            BrowserHistoryEnabled = config.BrowserHistoryEnabled,
            ScreenshotPeriodSeconds = config.ScreenshotPeriodSeconds,
            ScreenshotQuality = config.ScreenshotQuality.ToString(),
            ConfigVersion = config.ConfigVersion
        };
    }

    private void Reject(NamedPipeServerStream server, string reason)
    {
        Interlocked.Increment(ref _rejectedFrames);
        _logger.LogWarning("Capture pipe rejected a frame: {Reason}", reason);
        Reply(server, new SessionHelperReply { Ok = false, Error = reason });
    }

    private static void Reply(NamedPipeServerStream server, SessionHelperReply reply)
        => SessionHelperPipeCodec.Write(server, reply);

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);

        // Wake every listener with a real connection rather than cancelling its overlapped accept —
        // see ListenAsync for why cancellation there kills the process. One breaker per instance,
        // because each one satisfies exactly one waiting accept.
        for (var i = 0; i < ServerInstances; i++)
        {
            try
            {
                using var breaker = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut);
                breaker.Connect(500);
            }
            catch (Exception ex)
            {
                // Nothing listening, or it already exited. Either way the loop is going down.
                _logger.LogDebug(ex, "Capture pipe shutdown breaker on {Pipe}", _pipeName);
            }
        }

        if (_listeners is not null)
        {
            // Bounded: a thread parked in a blocking read on a still-open connection will not return
            // here. It is a background thread, so it cannot hold up process exit, and with no
            // overlapped I/O involved there is nothing for it to corrupt on the way out.
            foreach (var listener in _listeners)
            {
                listener.Join(TimeSpan.FromSeconds(2));
            }
        }

        _cts.Dispose();
        _cts = null;
        _listeners = null;
    }
}
