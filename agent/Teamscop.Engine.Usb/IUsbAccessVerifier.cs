namespace Teamscop.Engine.Usb;

public interface IUsbAccessVerifier
{
    /// <summary>Throws when the code is not accepted. Returning is the approval.</summary>
    Task ApproveSessionAsync(string deviceKey, string totpCode, string? deviceInstanceId, CancellationToken ct = default);

    /// <summary>
    /// True when the decision was made on this machine with no server round trip (§9.6). Recorded
    /// in the audit event so the admin can tell an offline grant from an online one.
    /// </summary>
    bool VerifiesOffline => false;
}
