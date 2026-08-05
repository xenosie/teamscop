namespace Teamscop.Engine.Usb;

public sealed record UsbStorageDevice(
    string InstanceId,
    string? FriendlyName,
    string? DriveLetter,
    DateTimeOffset ArrivedAt);

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
