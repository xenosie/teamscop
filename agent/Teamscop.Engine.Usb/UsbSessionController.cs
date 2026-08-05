using System.Runtime.InteropServices;
using Teamscop.Engine.Sync;

namespace Teamscop.Engine.Usb;

/// <summary>
/// Blocks USB mass storage by default; on insert shows sticker; on valid TOTP
/// lifts block for this insert/session only; re-blocks on removal.
/// </summary>
public sealed class UsbSessionController : IAsyncDisposable
{
    private readonly IRemovableStoragePolicy _policy;
    private readonly IUsbDeviceWatcher _watcher;
    private readonly IUsbApprovalPrompt _prompt;
    private readonly IUsbAccessVerifier _verifier;
    private readonly IOutboxQueue? _outbox;
    private readonly Func<string?> _deviceKey;
    private readonly Func<string> _apiBase;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _sessionInstanceId;
    private UsbSessionState _state = UsbSessionState.Idle;
    private int _promptInFlight;

    public UsbSessionController(
        IRemovableStoragePolicy policy,
        IUsbDeviceWatcher watcher,
        IUsbApprovalPrompt prompt,
        IUsbAccessVerifier verifier,
        Func<string?> deviceKey,
        Func<string> apiBase,
        IOutboxQueue? outbox = null)
    {
        _policy = policy;
        _watcher = watcher;
        _prompt = prompt;
        _verifier = verifier;
        _deviceKey = deviceKey;
        _apiBase = apiBase;
        _outbox = outbox;
    }

    public UsbSessionState State => _state;
    public string? SessionInstanceId => _sessionInstanceId;
    public bool PolicyBlocked => _policy.IsBlocked;
    public bool PolicySupported => _policy.IsSupported;

    public async Task StartAsync(CancellationToken ct = default)
    {
        try
        {
            _policy.ApplyBlock();
        }
        catch
        {
            // Non-admin / non-Windows: continue watching; approval path still works in tests.
        }

        _watcher.StorageArrived += OnArrived;
        _watcher.StorageRemoved += OnRemoved;
        await _watcher.StartAsync(ct).ConfigureAwait(false);
        await EnqueueAsync(AgentEventTypes.UsbEvent, new { action = "block_applied", supported = _policy.IsSupported }, ct)
            .ConfigureAwait(false);
    }

    private void OnArrived(UsbStorageDevice device)
    {
        _ = HandleArrivalAsync(device);
    }

    private void OnRemoved(UsbStorageDevice device)
    {
        _ = HandleRemovalAsync(device);
    }

    private async Task HandleArrivalAsync(UsbStorageDevice device)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // New device while a different session is open → end session and re-block.
            if (_state == UsbSessionState.SessionAllowed
                && !string.Equals(_sessionInstanceId, device.InstanceId, StringComparison.OrdinalIgnoreCase))
            {
                _policy.ApplyBlock();
                _sessionInstanceId = null;
                _state = UsbSessionState.Idle;
            }

            if (_state == UsbSessionState.SessionAllowed
                && string.Equals(_sessionInstanceId, device.InstanceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _state = UsbSessionState.BlockedPendingApproval;
            if (!_policy.IsBlocked)
            {
                try { _policy.ApplyBlock(); } catch { /* ignore */ }
            }

            await EnqueueAsync(AgentEventTypes.UsbEvent, new
            {
                action = "inserted_blocked",
                instanceId = device.InstanceId,
                friendlyName = device.FriendlyName,
                driveLetter = device.DriveLetter
            }).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (Interlocked.CompareExchange(ref _promptInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await PromptAndMaybeUnlockAsync(device).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _promptInFlight, 0);
        }
    }

    private async Task PromptAndMaybeUnlockAsync(UsbStorageDevice device)
    {
        var deviceKey = _deviceKey();
        if (string.IsNullOrWhiteSpace(deviceKey))
        {
            return;
        }

        var request = new UsbApprovalRequest(
            Guid.NewGuid().ToString("N"),
            deviceKey,
            _apiBase(),
            device,
            "USB storage is blocked. Enter the admin 6-digit code to approve this insert.");

        string? code;
        try
        {
            code = await _prompt.PromptForTotpAsync(request).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            await EnqueueAsync(AgentEventTypes.UsbEvent, new
            {
                action = "approval_cancelled",
                instanceId = device.InstanceId
            }).ConfigureAwait(false);
            return;
        }

        try
        {
            await _verifier.ApproveSessionAsync(deviceKey, code.Trim(), device.InstanceId).ConfigureAwait(false);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _policy.LiftBlock();
                _sessionInstanceId = device.InstanceId;
                _state = UsbSessionState.SessionAllowed;
            }
            finally
            {
                _gate.Release();
            }

            await EnqueueAsync(AgentEventTypes.UsbEvent, new
            {
                action = "session_allowed",
                instanceId = device.InstanceId,
                friendlyName = device.FriendlyName
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await EnqueueAsync(AgentEventTypes.UsbEvent, new
            {
                action = "approval_failed",
                instanceId = device.InstanceId,
                error = ex.Message
            }).ConfigureAwait(false);
        }
    }

    private async Task HandleRemovalAsync(UsbStorageDevice device)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var wasSession = string.Equals(_sessionInstanceId, device.InstanceId, StringComparison.OrdinalIgnoreCase);
            if (wasSession || _state != UsbSessionState.Idle)
            {
                try { _policy.ApplyBlock(); } catch { /* ignore */ }
                _sessionInstanceId = null;
                _state = UsbSessionState.Idle;
            }

            await EnqueueAsync(AgentEventTypes.UsbEvent, new
            {
                action = wasSession ? "session_ended_removed" : "removed_while_blocked",
                instanceId = device.InstanceId
            }).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task EnqueueAsync(string type, object payload, CancellationToken ct = default)
    {
        if (_outbox is null)
        {
            return Task.CompletedTask;
        }

        return _outbox.EnqueueAsync(OutboxItem.Create(type, payload), ct);
    }

    public static IRemovableStoragePolicy CreatePolicy()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new WindowsRemovableStoragePolicy()
            : new NullRemovableStoragePolicy();

    public async ValueTask DisposeAsync()
    {
        _watcher.StorageArrived -= OnArrived;
        _watcher.StorageRemoved -= OnRemoved;
        await _watcher.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
