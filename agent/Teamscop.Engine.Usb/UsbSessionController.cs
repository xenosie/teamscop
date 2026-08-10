using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Teamscop.Engine.Sync;

namespace Teamscop.Engine.Usb;

/// <summary>
/// USB mass storage is blocked for everyone, always (§9.4). On insert a sticker asks for the
/// admin's 6-digit code; a correct code enables that one device until it is unplugged (§9.5).
///
/// Two ordering rules make the guarantees hold, and both replace bugs:
/// <list type="bullet">
/// <item>Grants are keyed on <see cref="UsbStorageDevice.GrantKey"/> — the device's own identity.
/// Swapping sticks inside one poll interval cannot inherit an approval, because the second stick
/// is a different device, not a re-used drive letter.</item>
/// <item>Prompts are queued, not deduplicated. A second stick inserted while a sticker is open gets
/// its own sticker afterwards instead of being silently dropped by an in-flight flag.</item>
/// </list>
/// Arrivals and removals are drained by one pump so they are handled in the order they happened;
/// prompting runs on a second pump so pulling a stick mid-sticker is still noticed immediately.
/// </summary>
public sealed class UsbSessionController : IAsyncDisposable
{
    private enum SignalKind
    {
        Arrived,
        Removed
    }

    private readonly record struct Signal(SignalKind Kind, UsbStorageDevice Device);

    private readonly IRemovableStorageFloor _floor;
    private readonly IDeviceGate _gate;
    private readonly IUsbDeviceWatcher _watcher;
    private readonly IUsbApprovalPrompt _prompt;
    private readonly IUsbAccessVerifier _verifier;
    private readonly IOutboxQueue? _outbox;
    private readonly Func<string?> _deviceKey;
    private readonly Func<string> _apiBase;
    private readonly Func<(string BusinessLocal, string TimeZoneId, long ClockVersion, bool Synchronized)?>? _businessStamp;

    private readonly Channel<Signal> _signals =
        Channel.CreateUnbounded<Signal>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Channel<UsbStorageDevice> _prompts =
        Channel.CreateUnbounded<UsbStorageDevice>(new UnboundedChannelOptions { SingleReader = true });

    private readonly SemaphoreSlim _stateGate = new(1, 1);
    /// <summary>
    /// Attached devices by grant key. One physical stick can enumerate as SEVERAL device nodes —
    /// a multi-LUN drive, or a card reader with four slots — and every one of them must be gated,
    /// not just whichever arrived last. Holding a single device per key silently left the siblings
    /// enabled, so approving one slot exposed the rest.
    /// </summary>
    private readonly Dictionary<string, PresentDevice> _present = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _gateFailures = new(StringComparer.OrdinalIgnoreCase);

    private string? _grantedKey;
    private string? _grantedInstanceId;
    private UsbSessionState _state = UsbSessionState.Idle;
    private CancellationTokenSource? _cts;
    private Task? _signalPump;
    private Task? _promptPump;

    public UsbSessionController(
        IRemovableStorageFloor floor,
        IUsbDeviceWatcher watcher,
        IUsbApprovalPrompt prompt,
        IUsbAccessVerifier verifier,
        Func<string?> deviceKey,
        Func<string> apiBase,
        IDeviceGate? gate = null,
        IOutboxQueue? outbox = null,
        Func<(string BusinessLocal, string TimeZoneId, long ClockVersion, bool Synchronized)?>? businessStamp = null)
    {
        _floor = floor;
        _gate = gate ?? new NullDeviceGate();
        _watcher = watcher;
        _prompt = prompt;
        _verifier = verifier;
        _deviceKey = deviceKey;
        _apiBase = apiBase;
        _outbox = outbox;
        _businessStamp = businessStamp;
    }

    public UsbSessionState State => _state;

    /// <summary>Device instance path of the approved stick, or null when nothing is approved.</summary>
    public string? SessionInstanceId => _grantedInstanceId;

    public bool FloorApplied => _floor.IsBlocked;
    public bool FloorSupported => _floor.IsSupported;

    /// <summary>False on a machine where the device node cannot be disabled — §9.2 is then unmet.</summary>
    public bool GateSupported => _gate.IsSupported;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_signalPump is not null)
        {
            return;
        }

        try
        {
            _floor.ApplyBlock();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            // Non-admin / non-Windows: keep watching. The gate and the approval path still work.
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _signalPump = Task.Run(() => PumpSignalsAsync(_cts.Token), CancellationToken.None);
        _promptPump = Task.Run(() => PumpPromptsAsync(_cts.Token), CancellationToken.None);

        _watcher.StorageArrived += OnArrived;
        _watcher.StorageRemoved += OnRemoved;

        // The watcher's first scan reports everything already attached, so devices present at
        // service start are blocked by the same path as a fresh insert — no separate sweep needed.
        await _watcher.StartAsync(_cts.Token).ConfigureAwait(false);
        await EnqueueAsync(AgentEventTypes.UsbEvent, new
        {
            action = "block_applied",
            floorSupported = _floor.IsSupported,
            gateSupported = _gate.IsSupported
        }, ct).ConfigureAwait(false);
    }

    private void OnArrived(UsbStorageDevice device) => _signals.Writer.TryWrite(new Signal(SignalKind.Arrived, device));

    private void OnRemoved(UsbStorageDevice device) => _signals.Writer.TryWrite(new Signal(SignalKind.Removed, device));

    private async Task PumpSignalsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var signal in _signals.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (signal.Kind == SignalKind.Arrived)
                {
                    await HandleArrivalAsync(signal.Device, ct).ConfigureAwait(false);
                }
                else
                {
                    await HandleRemovalAsync(signal.Device, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task HandleArrivalAsync(UsbStorageDevice device, CancellationToken ct)
    {
        bool alreadyGranted;
        await _stateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_present.TryGetValue(device.GrantKey, out var group))
            {
                group = new PresentDevice(device);
                _present[device.GrantKey] = group;
            }

            group.Track(device);

            // Same physical stick re-enumerating (multi-LUN, or the node coming back after Allow).
            alreadyGranted = _grantedKey is not null
                             && string.Equals(_grantedKey, device.GrantKey, StringComparison.OrdinalIgnoreCase);
            if (alreadyGranted)
            {
                AllowNode(device.InstanceId);
            }
            else
            {
                // One stick at a time: a different device ends whatever grant is open.
                if (_grantedKey is not null)
                {
                    EndGrantLocked();
                }

                BlockNode(device.InstanceId);
                if (!_floor.IsBlocked)
                {
                    TryApplyFloor();
                }

                _state = UsbSessionState.BlockedPendingApproval;
            }
        }
        finally
        {
            _stateGate.Release();
        }

        if (alreadyGranted)
        {
            return;
        }

        await EnqueueAsync(AgentEventTypes.UsbEvent, new
        {
            action = "inserted_blocked",
            instanceId = device.InstanceId,
            containerId = device.ContainerId,
            friendlyName = device.FriendlyName,
            driveLetter = device.DriveLetter
        }, ct).ConfigureAwait(false);

        _prompts.Writer.TryWrite(device);
    }

    private async Task HandleRemovalAsync(UsbStorageDevice device, CancellationToken ct)
    {
        bool wasSession;
        await _stateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Only the node that actually went away. The device is gone once its last node is.
            if (_present.TryGetValue(device.GrantKey, out var group) && group.Forget(device.InstanceId))
            {
                _present.Remove(device.GrantKey);
            }

            wasSession = _grantedKey is not null
                         && string.Equals(_grantedKey, device.GrantKey, StringComparison.OrdinalIgnoreCase);

            if (wasSession)
            {
                EndGrantLocked();
            }
            else if (_present.Count == 0)
            {
                _state = UsbSessionState.Idle;
            }

            // Re-disable while the device is absent. DICS_FLAG_CONFIGSPECIFIC persists that in the
            // device's registry key, so the next insert is blocked before enumeration finishes —
            // without this an approved stick would come back enabled.
            BlockNode(device.InstanceId);
        }
        finally
        {
            _stateGate.Release();
        }

        await EnqueueAsync(AgentEventTypes.UsbEvent, new
        {
            action = wasSession ? "session_ended_removed" : "removed_while_blocked",
            instanceId = device.InstanceId,
            containerId = device.ContainerId
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Disable one node, and record it if the gate refuses. A discarded result meant a device the
    /// gate could not touch — a UAS-mode drive, or one whose removal-policy property will not read
    /// back — was reported to the admin as blocked while being fully usable.
    /// </summary>
    private void BlockNode(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || _gate.Block(instanceId))
        {
            return;
        }

        _gateFailures.Add(instanceId);
        _ = EnqueueAsync(AgentEventTypes.UsbEvent, new
        {
            action = "gate_block_failed",
            instanceId
        }, CancellationToken.None);
    }

    private void AllowNode(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        _gateFailures.Remove(instanceId);
        _gate.Allow(instanceId);
    }

    /// <summary>Nodes the gate could not disable — the sticker and app-history must not claim these are blocked.</summary>
    public IReadOnlyCollection<string> UngatedDevices
    {
        get
        {
            _stateGate.Wait();
            try
            {
                return _gateFailures.ToArray();
            }
            finally
            {
                _stateGate.Release();
            }
        }
    }

    /// <summary>Caller must hold <see cref="_stateGate"/>.</summary>
    private void EndGrantLocked()
    {
        // Re-block every node of the granted device, not only the one that was approved.
        if (_grantedKey is { } key && _present.TryGetValue(key, out var group))
        {
            foreach (var id in group.InstanceIds)
            {
                BlockNode(id);
            }
        }
        else if (_grantedInstanceId is { } instanceId)
        {
            BlockNode(instanceId);
        }

        _grantedKey = null;
        _grantedInstanceId = null;
        TryApplyFloor();
        _state = _present.Count == 0 ? UsbSessionState.Idle : UsbSessionState.BlockedPendingApproval;
    }

    private void TryApplyFloor()
    {
        try
        {
            _floor.ApplyBlock();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            // Already reported at start; re-logging every insert would be noise.
        }
    }

    private async Task PumpPromptsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var device in _prompts.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (!await IsStillWaitingAsync(device, ct).ConfigureAwait(false))
                {
                    continue;
                }

                await PromptAndMaybeUnlockAsync(device, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>The device is still attached and still unapproved — otherwise its sticker is stale.</summary>
    private async Task<bool> IsStillWaitingAsync(UsbStorageDevice device, CancellationToken ct)
    {
        await _stateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _present.TryGetValue(device.GrantKey, out var current)
                   && current.Contains(device.InstanceId)
                   && !string.Equals(_grantedKey, device.GrantKey, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task PromptAndMaybeUnlockAsync(UsbStorageDevice device, CancellationToken ct)
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
            code = await _prompt.PromptForTotpAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await EnqueueAsync(AgentEventTypes.UsbEvent, new
            {
                action = "approval_failed",
                instanceId = device.InstanceId,
                error = ex.Message
            }, ct).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            await EnqueueAsync(AgentEventTypes.UsbEvent, new
            {
                action = "approval_cancelled",
                instanceId = device.InstanceId
            }, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await _verifier.ApproveSessionAsync(deviceKey, code.Trim(), device.InstanceId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await EnqueueAsync(AgentEventTypes.UsbEvent, new
            {
                action = "approval_failed",
                instanceId = device.InstanceId,
                error = ex.Message
            }, ct).ConfigureAwait(false);
            return;
        }

        bool granted;
        await _stateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The stick can be pulled while the sticker is open; a correct code must not open the
            // machine for whatever is plugged in next.
            granted = _present.TryGetValue(device.GrantKey, out var current)
                      && current.Contains(device.InstanceId);
            if (granted)
            {
                // Approval is for the physical device, so every node it presents opens together.
                foreach (var id in current!.InstanceIds)
                {
                    AllowNode(id);
                }

                _floor.LiftBlock();
                _grantedKey = device.GrantKey;
                _grantedInstanceId = device.InstanceId;
                _state = UsbSessionState.SessionAllowed;
            }
        }
        finally
        {
            _stateGate.Release();
        }

        await EnqueueAsync(AgentEventTypes.UsbEvent, new
        {
            action = granted ? "session_allowed" : "approval_stale_device_removed",
            instanceId = device.InstanceId,
            containerId = device.ContainerId,
            friendlyName = device.FriendlyName,
            verifiedOffline = _verifier.VerifiesOffline
        }, ct).ConfigureAwait(false);
    }

    private Task EnqueueAsync(string type, object payload, CancellationToken ct = default)
    {
        if (_outbox is null)
        {
            return Task.CompletedTask;
        }

        var stamp = _businessStamp?.Invoke();
        if (stamp is not { } biz)
        {
            return _outbox.EnqueueAsync(OutboxItem.Create(type, payload), ct);
        }

        var node = JsonSerializer.SerializeToNode(payload)?.AsObject() ?? new JsonObject();
        node["businessLocal"] = biz.BusinessLocal;
        node["businessTimeZoneId"] = biz.TimeZoneId;
        node["businessClockVersion"] = biz.ClockVersion;
        node["businessSynchronized"] = biz.Synchronized;
        return _outbox.EnqueueAsync(new OutboxItem
        {
            EventType = type,
            PayloadJson = node.ToJsonString()
        }, ct);
    }

    public static IRemovableStorageFloor CreateFloor()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new WindowsRemovableStorageFloor()
            : new NullRemovableStoragePolicy();

    public static IDeviceGate CreateGate()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new WindowsDeviceGate()
            : new NullDeviceGate();

    public static IUsbDeviceWatcher CreateWatcher()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new WindowsUsbDeviceWatcher()
            : new PollingUsbDeviceWatcher();

    public async ValueTask DisposeAsync()
    {
        _watcher.StorageArrived -= OnArrived;
        _watcher.StorageRemoved -= OnRemoved;
        _signals.Writer.TryComplete();
        _prompts.Writer.TryComplete();

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        foreach (var pump in new[] { _signalPump, _promptPump })
        {
            if (pump is not null)
            {
                try { await pump.ConfigureAwait(false); } catch (OperationCanceledException) { /* expected */ }
            }
        }

        await _watcher.DisposeAsync().ConfigureAwait(false);
        _cts?.Dispose();
        _stateGate.Dispose();
    }

    /// <summary>
    /// Every device node one physical device currently presents. Grant and revocation apply to the
    /// whole set, so a multi-LUN stick cannot have one slot open while another stays shut.
    /// </summary>
    private sealed class PresentDevice(UsbStorageDevice first)
    {
        public UsbStorageDevice Latest { get; private set; } = first;

        public HashSet<string> InstanceIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Track(UsbStorageDevice device)
        {
            Latest = device;
            if (!string.IsNullOrWhiteSpace(device.InstanceId))
            {
                InstanceIds.Add(device.InstanceId);
            }
        }

        public bool Contains(string instanceId) => InstanceIds.Contains(instanceId);

        /// <summary>Drops one node. True when that was the last one and the device is really gone.</summary>
        public bool Forget(string instanceId)
        {
            InstanceIds.Remove(instanceId);
            return InstanceIds.Count == 0;
        }
    }
}
