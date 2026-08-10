using Teamscop.Engine.Lifecycle;

namespace Teamscop.Engine.Usb;

/// <summary>
/// A6 / §9.6 — the USB code is checked against the secret this machine holds, with no server call.
/// The audit record still reaches the server: <see cref="UsbSessionController"/> enqueues
/// <c>usb_event</c> to the durable outbox, which uploads whenever connectivity returns (§13.1).
/// </summary>
public sealed class LocalUsbAccessVerifier(IApprovalCodeVerifier verifier) : IUsbAccessVerifier
{
    public bool VerifiesOffline => true;

    public Task ApproveSessionAsync(
        string deviceKey,
        string totpCode,
        string? deviceInstanceId,
        CancellationToken ct = default)
    {
        var check = verifier.Verify(ApprovalPurpose.Usb, totpCode);
        return check.Ok
            ? Task.CompletedTask
            : Task.FromException(new UnauthorizedAccessException(check.Describe()));
    }
}
