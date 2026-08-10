using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Teamscop.Engine.Usb;

/// <summary>
/// A4 / §9.2 — blocks a USB stick by disabling its disk device node, so Windows never presents a
/// volume for it: no drive letter, nothing in Explorer, nothing in This PC, nothing in Disk
/// Management. Approval re-enables that one node and nothing else.
///
/// Requires SYSTEM or an administrator; <c>Teamscop.StaffService</c> runs as LocalSystem, so it
/// qualifies. Because the disable is written with <c>DICS_FLAG_CONFIGSPECIFIC</c> it persists in the
/// device's registry key — which is the property that matters most: after a stick has been seen
/// once, every later insert is blocked before enumeration completes, so it never appears at all.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDeviceGate : IDeviceGate
{
    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public bool Block(string deviceInstanceId)
        => !string.IsNullOrWhiteSpace(deviceInstanceId) && WindowsDeviceNodes.Disable(deviceInstanceId);

    public bool Allow(string deviceInstanceId)
        => !string.IsNullOrWhiteSpace(deviceInstanceId) && WindowsDeviceNodes.Enable(deviceInstanceId);

    public bool IsDeviceBlocked(string deviceInstanceId)
        => !string.IsNullOrWhiteSpace(deviceInstanceId) && WindowsDeviceNodes.IsNodeDisabled(deviceInstanceId);
}
