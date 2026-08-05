using Teamscop.Engine.Lifecycle;

namespace Teamscop.Engine.Usb;

public sealed class LifecycleUsbAccessVerifier(LifecycleApiClient api) : IUsbAccessVerifier
{
    public async Task ApproveSessionAsync(
        string deviceKey,
        string totpCode,
        string? deviceInstanceId,
        CancellationToken ct = default)
    {
        var approve = await api.VerifyUsbAsync(deviceKey, totpCode, deviceInstanceId, ct).ConfigureAwait(false);
        try
        {
            await api.ConsumeUsbTicketAsync(approve.UsbSessionTicket, ct).ConfigureAwait(false);
        }
        catch
        {
            // Local unlock is allowed once verify succeeded.
        }
    }
}
