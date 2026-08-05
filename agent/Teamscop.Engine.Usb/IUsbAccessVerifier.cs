namespace Teamscop.Engine.Usb;

public interface IUsbAccessVerifier
{
    Task ApproveSessionAsync(string deviceKey, string totpCode, string? deviceInstanceId, CancellationToken ct = default);
}
