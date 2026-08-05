namespace Teamscop.Engine.Usb;

public interface IUsbDeviceWatcher : IAsyncDisposable
{
    event Action<UsbStorageDevice>? StorageArrived;
    event Action<UsbStorageDevice>? StorageRemoved;
    Task StartAsync(CancellationToken ct = default);
}
