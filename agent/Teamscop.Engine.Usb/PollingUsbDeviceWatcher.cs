namespace Teamscop.Engine.Usb;

/// <summary>
/// The watcher used off Windows and in tests. It observes nothing on its own — arrivals and
/// removals are driven by <see cref="SimulateArrival"/> and <see cref="SimulateRemoval"/>.
///
/// It used to poll <c>DriveInfo</c> and synthesise <c>"REMOVABLE\" + driveRoot</c> as a device
/// identity. That is deleted, not ported: a drive letter is not an identity (§9.5), and once
/// <see cref="WindowsDeviceGate"/> blocks a stick there is no letter to see either.
/// </summary>
public sealed class PollingUsbDeviceWatcher : IUsbDeviceWatcher
{
    private readonly Dictionary<string, UsbStorageDevice> _devices = new(StringComparer.OrdinalIgnoreCase);

    public event Action<UsbStorageDevice>? StorageArrived;
    public event Action<UsbStorageDevice>? StorageRemoved;

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

    public void SimulateArrival(UsbStorageDevice device)
    {
        lock (_devices)
        {
            if (!_devices.TryAdd(device.InstanceId, device))
            {
                return;
            }
        }

        StorageArrived?.Invoke(device);
    }

    public void SimulateRemoval(string instanceId)
    {
        UsbStorageDevice? device;
        lock (_devices)
        {
            if (!_devices.Remove(instanceId, out device))
            {
                return;
            }
        }

        StorageRemoved?.Invoke(device);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
