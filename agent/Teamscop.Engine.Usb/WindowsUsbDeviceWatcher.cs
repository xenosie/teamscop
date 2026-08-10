using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Teamscop.Engine.Usb;

/// <summary>
/// Watches USB mass-storage <b>device nodes</b> — not drive letters, which is the bug this
/// replaces. A stick that is gated has no drive letter at all, so a drive-based watcher would go
/// blind exactly when the block works.
///
/// One second, not two: the poll interval is how long a never-before-seen stick can be visible
/// before the gate disables it, and enumerating one setup class is a few registry reads, which is
/// affordable on the hardware §15.2 targets. Every stick seen once stays disabled in the registry,
/// so this window exists only on a device's very first insert on this PC.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUsbDeviceWatcher : IUsbDeviceWatcher
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly Dictionary<string, UsbStorageDevice> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public WindowsUsbDeviceWatcher(TimeSpan? pollInterval = null)
    {
        _interval = pollInterval ?? DefaultInterval;
    }

    public event Action<UsbStorageDevice>? StorageArrived;
    public event Action<UsbStorageDevice>? StorageRemoved;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || _loop is not null)
        {
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => PollLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                ScanOnce();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A transient SetupAPI failure must not stop the watcher; the next tick retries.
            }

            try
            {
                await Task.Delay(_interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void ScanOnce()
    {
        var present = WindowsDeviceNodes.ListPresent();
        var seenNow = new HashSet<string>(present.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var node in present)
        {
            seenNow.Add(node.InstanceId);
            if (_seen.ContainsKey(node.InstanceId))
            {
                continue;
            }

            var device = new UsbStorageDevice(
                node.InstanceId,
                node.FriendlyName,
                DriveLetter: null,
                DateTimeOffset.UtcNow,
                node.ContainerId);
            _seen[node.InstanceId] = device;
            StorageArrived?.Invoke(device);
        }

        foreach (var instanceId in _seen.Keys.ToArray())
        {
            if (seenNow.Contains(instanceId))
            {
                continue;
            }

            if (_seen.Remove(instanceId, out var gone))
            {
                StorageRemoved?.Invoke(gone);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { /* expected */ }
        }

        _cts.Dispose();
        _cts = null;
        _loop = null;
    }
}
