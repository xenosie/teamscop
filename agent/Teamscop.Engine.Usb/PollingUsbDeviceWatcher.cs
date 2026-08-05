using System.Runtime.InteropServices;

namespace Teamscop.Engine.Usb;

/// <summary>
/// Polls for USB mass-storage (removable drives). HID mouse/keyboard never appear as DriveType.Removable.
/// </summary>
public sealed class PollingUsbDeviceWatcher : IUsbDeviceWatcher
{
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UsbStorageDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action<UsbStorageDevice>? StorageArrived;
    public event Action<UsbStorageDevice>? StorageRemoved;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
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
            catch
            {
                // ignore transient drive enumeration errors
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void ScanOnce()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Removable)
                {
                    continue;
                }

                // Skip not-ready (just ejected / policy-blocked) unless we already track it.
                var root = drive.Name.TrimEnd('\\', '/');
                var instanceId = "REMOVABLE\\" + root.ToUpperInvariant();
                string? friendly = null;
                try
                {
                    if (drive.IsReady)
                    {
                        friendly = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                            ? drive.DriveFormat
                            : drive.VolumeLabel;
                    }
                }
                catch
                {
                    // policy-blocked volumes may throw on metadata
                }

                seen.Add(instanceId);
                if (_known.Add(instanceId))
                {
                    var device = new UsbStorageDevice(instanceId, friendly, root, DateTimeOffset.UtcNow);
                    _devices[instanceId] = device;
                    StorageArrived?.Invoke(device);
                }
            }
            catch
            {
                // ignore per-drive errors
            }
        }

        foreach (var id in _known.ToArray())
        {
            if (!seen.Contains(id))
            {
                _known.Remove(id);
                if (_devices.Remove(id, out var device))
                {
                    StorageRemoved?.Invoke(device);
                }
            }
        }
    }

    /// <summary>Test helper: simulate a USB storage insert on any OS.</summary>
    public void SimulateArrival(UsbStorageDevice device)
    {
        if (_known.Add(device.InstanceId))
        {
            _devices[device.InstanceId] = device;
            StorageArrived?.Invoke(device);
        }
    }

    /// <summary>Test helper: simulate USB storage removal.</summary>
    public void SimulateRemoval(string instanceId)
    {
        _known.Remove(instanceId);
        if (_devices.Remove(instanceId, out var device))
        {
            StorageRemoved?.Invoke(device);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            if (_loop is not null)
            {
                try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
            }
            _cts.Dispose();
        }
    }
}
