namespace Teamscop.Engine.Usb;

/// <summary>
/// One USB mass-storage device, identified the way Windows identifies it (A5, §9.5).
///
/// <paramref name="InstanceId"/> is the device instance path — e.g.
/// <c>USBSTOR\DISK&amp;VEN_SANDISK&amp;PROD_ULTRA&amp;REV_1.00\4C530001110608116542&amp;0</c> — which
/// contains the stick's own serial. It is never a drive letter: a letter is a name Windows hands out
/// and reassigns, so a grant keyed on one is inherited by the next stick in the same slot.
/// <paramref name="DriveLetter"/> is display metadata only, and is normally null because a gated
/// device has no letter to show.
/// </summary>
public sealed record UsbStorageDevice(
    string InstanceId,
    string? FriendlyName,
    string? DriveLetter,
    DateTimeOffset ArrivedAt,
    string? ContainerId = null)
{
    /// <summary>
    /// What a grant is keyed on. The container id is Windows' "one physical device" identity and
    /// stays the same across a multi-LUN stick's several disk nodes; the instance path is the
    /// fallback when Windows reports no container.
    /// </summary>
    public string GrantKey => string.IsNullOrWhiteSpace(ContainerId) ? InstanceId : ContainerId;
}

public enum UsbSessionState
{
    Idle = 0,
    BlockedPendingApproval = 1,
    SessionAllowed = 2
}

public sealed record UsbApprovalRequest(
    string RequestId,
    string DeviceKey,
    string ApiBaseUrl,
    UsbStorageDevice Device,
    string Message);

public sealed record UsbApprovalResponse(
    string RequestId,
    bool Approved,
    string? TotpCode,
    string? Error);
