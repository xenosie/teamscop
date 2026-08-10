using System.Text.Json;
using Teamscop.Engine.Sync;
using Teamscop.Engine.Usb;

namespace Teamscop.Api.Tests;

/// <summary>
/// C2 / A4 / A5 / §9.2 / §9.5 — the three USB cases that were defects, and one that would be the
/// worst kind of defect if it came back.
///
/// <see cref="UsbSessionControllerTests"/> covers one stick, one code. These cover what the state
/// machine was actually rebuilt for: a device that presents several nodes, a stick swapped inside a
/// single poll window, and a second stick arriving while a sticker is already on screen.
///
/// Nothing here waits on wall time. The controller enqueues an audit event at every decision point,
/// so <see cref="RecordingOutbox"/> is used as the synchronisation primitive: awaiting
/// <c>session_allowed</c> is awaiting the grant itself, not sleeping and hoping.
/// </summary>
public class UsbMultiNodeSessionTests
{
    private const string Container = "{1e0f2c34-0000-0000-0000-aaaaaaaaaaaa}";

    /// <summary>A multi-LUN stick, or a card reader: one physical device, several disk nodes.</summary>
    private static UsbStorageDevice Node(int lun, string container = Container)
        => new($"USBSTOR\\DISK&VEN_SANDISK&PROD_ULTRA\\4C530001110608116542&{lun}",
            "SanDisk Ultra",
            DriveLetter: null,
            DateTimeOffset.UtcNow,
            container);

    [Fact]
    public async Task EveryNodeOfOneDeviceIsGatedTogether_AndOpensOnASingleCode()
    {
        var kit = new UsbTestKit();
        await using var controller = kit.Build();
        await controller.StartAsync();
        await kit.Outbox.WaitForAsync("block_applied");

        var lun0 = Node(0);
        var lun1 = Node(1);
        kit.Watcher.SimulateArrival(lun0);
        kit.Watcher.SimulateArrival(lun1);
        await kit.Outbox.WaitForAsync("inserted_blocked");
        await kit.Outbox.WaitForAsync("inserted_blocked");

        // §9.2 — before any code is entered, no node of the device is usable. Gating only the node
        // that happened to arrive last left the siblings mounted, so approving one slot of a reader
        // exposed the rest.
        Assert.True(kit.Gate.IsDeviceBlocked(lun0.InstanceId));
        Assert.True(kit.Gate.IsDeviceBlocked(lun1.InstanceId));
        Assert.Equal(UsbSessionState.BlockedPendingApproval, controller.State);

        var request = await kit.Prompt.OpenedAsync(lun0.InstanceId);
        Assert.Equal(lun0.InstanceId, request.Device.InstanceId);
        kit.Prompt.Answer(lun0.InstanceId, "112233");
        await kit.Outbox.WaitForAsync("session_allowed");

        // §9.5 — the grant is on the physical device, so all of its nodes open on the one code.
        Assert.Equal(UsbSessionState.SessionAllowed, controller.State);
        Assert.False(kit.Gate.IsDeviceBlocked(lun0.InstanceId));
        Assert.False(kit.Gate.IsDeviceBlocked(lun1.InstanceId));
        Assert.Equal(lun0.InstanceId, controller.SessionInstanceId);
        Assert.False(controller.FloorApplied);
    }

    [Fact]
    public async Task ANodeThatEnumeratesLateJoinsTheOpenGrant_ThenClosesWithTheRest()
    {
        var kit = new UsbTestKit();
        await using var controller = kit.Build();
        await controller.StartAsync();
        await kit.Outbox.WaitForAsync("block_applied");

        var lun0 = Node(0);
        kit.Watcher.SimulateArrival(lun0);
        await kit.Prompt.OpenedAsync(lun0.InstanceId);
        kit.Prompt.Answer(lun0.InstanceId, "112233");
        await kit.Outbox.WaitForAsync("session_allowed");

        // Windows finishes enumerating the device after the approval. It is the same stick, so it
        // must open without a second sticker.
        var lun1 = Node(1);
        kit.Watcher.SimulateArrival(lun1);

        // One signal pump, read in order: pulling lun0 cannot be handled before lun1's arrival is.
        kit.Watcher.SimulateRemoval(lun0.InstanceId);
        await kit.Outbox.WaitForAsync("session_ended_removed");

        // Allowed on arrival with no prompt, then re-blocked with the rest when the stick was pulled.
        Assert.Equal(new[] { "allow", "block" }, kit.Gate.History(lun1.InstanceId));
        Assert.False(kit.Prompt.WasOpened(lun1.InstanceId));
        Assert.True(kit.Gate.IsDeviceBlocked(lun0.InstanceId));
        Assert.True(kit.Gate.IsDeviceBlocked(lun1.InstanceId));
        Assert.True(controller.FloorApplied);
        Assert.Null(controller.SessionInstanceId);

        kit.Watcher.SimulateRemoval(lun1.InstanceId);
        await kit.Outbox.WaitForAsync("removed_while_blocked");
        Assert.Equal(UsbSessionState.Idle, controller.State);
    }

    [Fact]
    public async Task AStickSwappedWhileTheStickerIsOpen_CannotInheritTheApproval()
    {
        var kit = new UsbTestKit();
        await using var controller = kit.Build();
        await controller.StartAsync();
        await kit.Outbox.WaitForAsync("block_applied");

        var first = Node(0);
        kit.Watcher.SimulateArrival(first);
        await kit.Outbox.WaitForAsync("inserted_blocked");
        await kit.Prompt.OpenedAsync(first.InstanceId);

        // The employee pulls the approved-pending stick and pushes in a different one, reusing the
        // same slot and, on Windows, very likely the same drive letter. A grant keyed on a letter
        // would hand the second stick the first stick's approval.
        kit.Watcher.SimulateRemoval(first.InstanceId);
        await kit.Outbox.WaitForAsync("removed_while_blocked");

        var second = Node(0, container: "{2b0f2c34-0000-0000-0000-bbbbbbbbbbbb}") with
        {
            InstanceId = "USBSTOR\\DISK&VEN_KINGSTON&PROD_DT101\\0019E06B9F60&0"
        };
        kit.Watcher.SimulateArrival(second);
        await kit.Outbox.WaitForAsync("inserted_blocked");

        // Now the correct code for the first stick arrives. It must open nothing.
        kit.Prompt.Answer(first.InstanceId, "112233");
        var stale = await kit.Outbox.WaitForAsync("approval_stale_device_removed");

        Assert.Equal(first.InstanceId, stale.GetProperty("instanceId").GetString());
        Assert.NotEqual(UsbSessionState.SessionAllowed, controller.State);
        Assert.Null(controller.SessionInstanceId);
        Assert.True(kit.Gate.IsDeviceBlocked(second.InstanceId));
        Assert.True(controller.FloorApplied);

        // And the stick that is actually plugged in gets its own sticker.
        var request = await kit.Prompt.OpenedAsync(second.InstanceId);
        Assert.Equal(second.InstanceId, request.Device.InstanceId);
    }

    [Fact]
    public async Task ASecondStickInsertedWhileAStickerIsOpen_GetsItsOwnSticker()
    {
        var kit = new UsbTestKit();
        await using var controller = kit.Build();
        await controller.StartAsync();
        await kit.Outbox.WaitForAsync("block_applied");

        var first = Node(0);
        kit.Watcher.SimulateArrival(first);
        await kit.Outbox.WaitForAsync("inserted_blocked");
        await kit.Prompt.OpenedAsync(first.InstanceId);

        var second = Node(0, container: "{2b0f2c34-0000-0000-0000-bbbbbbbbbbbb}") with
        {
            InstanceId = "USBSTOR\\DISK&VEN_KINGSTON&PROD_DT101\\0019E06B9F60&0"
        };
        kit.Watcher.SimulateArrival(second);
        await kit.Outbox.WaitForAsync("inserted_blocked");
        Assert.True(kit.Gate.IsDeviceBlocked(second.InstanceId));

        kit.Prompt.Answer(first.InstanceId, "112233");
        await kit.Outbox.WaitForAsync("session_allowed");

        // Prompts are queued, not deduplicated. An in-flight flag would have dropped this one on the
        // floor, leaving a permanently gated stick with no way for the employee to ask about it.
        var request = await kit.Prompt.OpenedAsync(second.InstanceId);
        Assert.Equal(second.InstanceId, request.Device.InstanceId);

        kit.Prompt.Answer(second.InstanceId, "445566");
        await kit.Outbox.WaitForAsync("session_allowed");
        Assert.Equal(second.InstanceId, controller.SessionInstanceId);
        Assert.False(kit.Gate.IsDeviceBlocked(second.InstanceId));

        // Pulling either stick re-gates it, whichever one the open grant currently names.
        kit.Watcher.SimulateRemoval(first.InstanceId);
        await kit.Outbox.WaitForAsync("removed_while_blocked");
        Assert.True(kit.Gate.IsDeviceBlocked(first.InstanceId));
    }

    [Fact]
    public async Task AWrongCodeLeavesEveryNodeGated_AndTheStickerComesBack()
    {
        var kit = new UsbTestKit();
        kit.Verifier.Reject("000000");
        await using var controller = kit.Build();
        await controller.StartAsync();
        await kit.Outbox.WaitForAsync("block_applied");

        var lun0 = Node(0);
        var lun1 = Node(1);
        kit.Watcher.SimulateArrival(lun0);
        kit.Watcher.SimulateArrival(lun1);
        await kit.Outbox.WaitForAsync("inserted_blocked");
        await kit.Outbox.WaitForAsync("inserted_blocked");

        await kit.Prompt.OpenedAsync(lun0.InstanceId);
        kit.Prompt.Answer(lun0.InstanceId, "000000");
        var failure = await kit.Outbox.WaitForAsync("approval_failed");

        Assert.Equal(lun0.InstanceId, failure.GetProperty("instanceId").GetString());
        Assert.NotEqual(UsbSessionState.SessionAllowed, controller.State);
        Assert.True(kit.Gate.IsDeviceBlocked(lun0.InstanceId));
        Assert.True(kit.Gate.IsDeviceBlocked(lun1.InstanceId));
        Assert.True(controller.FloorApplied);

        // The queued sticker for the sibling node still runs, so the employee is not left with a
        // dead device and no way to retry.
        await kit.Prompt.OpenedAsync(lun1.InstanceId);
    }

    [Fact]
    public async Task ADeviceTheGateCannotDisable_IsReportedRatherThanCalledBlocked()
    {
        var kit = new UsbTestKit();
        var stubborn = Node(0);
        kit.Gate.RefuseBlockOf(stubborn.InstanceId);

        await using var controller = kit.Build();
        await controller.StartAsync();
        await kit.Outbox.WaitForAsync("block_applied");

        kit.Watcher.SimulateArrival(stubborn);
        var failed = await kit.Outbox.WaitForAsync("gate_block_failed");

        // A discarded gate result meant a fully usable stick — a UAS-mode drive, say — was shown to
        // the admin as blocked. §9.2 is unmet on that machine and the sticker has to say so.
        Assert.Equal(stubborn.InstanceId, failed.GetProperty("instanceId").GetString());
        Assert.Contains(stubborn.InstanceId, controller.UngatedDevices);
        Assert.True(controller.FloorApplied); // the machine-wide floor is all that is left
    }
}

/// <summary>Everything one controller needs, wired to doubles the test drives directly.</summary>
file sealed class UsbTestKit
{
    public RecordingOutbox Outbox { get; } = new();
    public RecordingDeviceGate Gate { get; } = new();
    public PollingUsbDeviceWatcher Watcher { get; } = new();
    public ControlledPrompt Prompt { get; } = new();
    public ControlledVerifier Verifier { get; } = new();
    public NullRemovableStoragePolicy Floor { get; } = new();

    public UsbSessionController Build()
        => new(
            Floor,
            Watcher,
            Prompt,
            Verifier,
            deviceKey: () => "staff-device",
            apiBase: () => "https://teamscop.com",
            gate: Gate,
            outbox: Outbox);
}

/// <summary>
/// The controller's audit trail, used as the test's clock. Every decision it makes ends in an
/// enqueued <c>usb_event</c>, so waiting for a named action is waiting for that exact decision.
/// </summary>
file sealed class RecordingOutbox : IOutboxQueue
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly List<(string Action, JsonElement Payload)> _seen = [];
    private readonly Dictionary<string, int> _consumed = new(StringComparer.Ordinal);
    private readonly List<(string Action, TaskCompletionSource<JsonElement> Waiter)> _waiting = [];

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _seen.Count;
            }
        }
    }

    public Task EnqueueAsync(OutboxItem item, CancellationToken cancellationToken = default)
    {
        using var doc = JsonDocument.Parse(item.PayloadJson);
        var payload = doc.RootElement.Clone();
        var action = payload.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";

        TaskCompletionSource<JsonElement>? ready = null;
        lock (_gate)
        {
            _seen.Add((action, payload));
            var index = _waiting.FindIndex(w => w.Action == action);
            if (index >= 0)
            {
                ready = _waiting[index].Waiter;
                _waiting.RemoveAt(index);
                _consumed[action] = _consumed.GetValueOrDefault(action) + 1;
            }
        }

        ready?.TrySetResult(payload);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Completes on the next occurrence of <paramref name="action"/> that no earlier wait has
    /// already taken, so two identical events can be awaited one after the other.
    /// </summary>
    public Task<JsonElement> WaitForAsync(string action)
    {
        TaskCompletionSource<JsonElement> waiter;
        lock (_gate)
        {
            var taken = _consumed.GetValueOrDefault(action);
            var match = _seen.Where(s => s.Action == action).Skip(taken).Select(s => s.Payload).ToList();
            if (match.Count > 0)
            {
                _consumed[action] = taken + 1;
                return Task.FromResult(match[0]);
            }

            waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Add((action, waiter));
        }

        // The bound is a deadlock guard, never a synchronisation step: the wait ends the moment the
        // controller enqueues, and a timeout here is a real failure to report.
        return waiter.Task.WaitAsync(Timeout);
    }

    public Task<IReadOnlyList<OutboxItem>> PeekPendingAsync(int take, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OutboxItem>>([]);

    public Task AcknowledgeAsync(IEnumerable<Guid> clientEventIds, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task MarkFailedAsync(Guid clientEventId, string error, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>
/// A device gate that remembers the order it was asked to block and allow each node, so a test can
/// assert on what happened to a node rather than only on where it ended up.
/// </summary>
file sealed class RecordingDeviceGate : IDeviceGate
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<string>> _history = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _refuseBlock = new(StringComparer.OrdinalIgnoreCase);

    public bool IsSupported => true;

    public void RefuseBlockOf(string instanceId)
    {
        lock (_gate)
        {
            _refuseBlock.Add(instanceId);
        }
    }

    public bool Block(string deviceInstanceId)
    {
        lock (_gate)
        {
            if (_refuseBlock.Contains(deviceInstanceId))
            {
                Record(deviceInstanceId, "block-refused");
                return false;
            }

            Record(deviceInstanceId, "block");
            _blocked.Add(deviceInstanceId);
            return true;
        }
    }

    public bool Allow(string deviceInstanceId)
    {
        lock (_gate)
        {
            Record(deviceInstanceId, "allow");
            _blocked.Remove(deviceInstanceId);
            return true;
        }
    }

    public bool IsDeviceBlocked(string deviceInstanceId)
    {
        lock (_gate)
        {
            return _blocked.Contains(deviceInstanceId);
        }
    }

    public IReadOnlyList<string> History(string deviceInstanceId)
    {
        lock (_gate)
        {
            return _history.TryGetValue(deviceInstanceId, out var h) ? h.ToArray() : [];
        }
    }

    private void Record(string instanceId, string what)
    {
        if (!_history.TryGetValue(instanceId, out var list))
        {
            list = [];
            _history[instanceId] = list;
        }

        list.Add(what);
    }
}

/// <summary>A sticker the test opens and answers by hand, one per device node.</summary>
file sealed class ControlledPrompt : IUsbApprovalPrompt
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<UsbApprovalRequest>> _opened = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskCompletionSource<string?>> _answers = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> PromptForTotpAsync(UsbApprovalRequest request, CancellationToken ct = default)
    {
        Slot(_opened, request.Device.InstanceId).TrySetResult(request);
        return Slot(_answers, request.Device.InstanceId).Task.WaitAsync(ct);
    }

    /// <summary>Completes when the sticker for this node is actually on screen.</summary>
    public Task<UsbApprovalRequest> OpenedAsync(string instanceId)
        => Slot(_opened, instanceId).Task.WaitAsync(TimeSpan.FromSeconds(30));

    public bool WasOpened(string instanceId)
    {
        lock (_gate)
        {
            return _opened.TryGetValue(instanceId, out var tcs) && tcs.Task.IsCompleted;
        }
    }

    public void Answer(string instanceId, string? code) => Slot(_answers, instanceId).TrySetResult(code);

    private TaskCompletionSource<T> Slot<T>(Dictionary<string, TaskCompletionSource<T>> map, string key)
    {
        lock (_gate)
        {
            if (!map.TryGetValue(key, out var tcs))
            {
                tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                map[key] = tcs;
            }

            return tcs;
        }
    }
}

/// <summary>Accepts every code except the ones the test names, the way the local verifier would.</summary>
file sealed class ControlledVerifier : IUsbAccessVerifier
{
    private readonly HashSet<string> _rejected = new(StringComparer.Ordinal);

    public bool VerifiesOffline => true;

    public void Reject(string code) => _rejected.Add(code);

    public Task ApproveSessionAsync(string deviceKey, string totpCode, string? deviceInstanceId, CancellationToken ct = default)
        => _rejected.Contains(totpCode)
            ? Task.FromException(new UnauthorizedAccessException("Incorrect code."))
            : Task.CompletedTask;
}
