using System.Runtime.InteropServices;

namespace Teamscop.Engine.Usb;

/// <summary>
/// Undoes everything the USB gate did to this machine.
///
/// Both halves of the block outlive the agent: the Removable Storage policy is a registry value,
/// and a disabled device node keeps <c>CONFIGFLAG_DISABLED</c> in the device's own key — that
/// persistence is the point during normal operation, and a liability at uninstall. Without this,
/// removing Teamscop would leave a PC on which USB storage never works again.
///
/// Called from the uninstall path, after the code has been accepted.
/// </summary>
public static class UsbGateRestore
{
    /// <summary>Number of device nodes re-enabled. Safe to call on a machine that was never gated.</summary>
    public static int RestoreMachine()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return 0;
        }

        try
        {
            new WindowsRemovableStorageFloor().LiftBlock();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            // Needs admin; the uninstaller has it, anything else is best effort.
        }

        var enabled = 0;
        foreach (var node in WindowsDeviceNodes.ListPresent())
        {
            if (node.Disabled && WindowsDeviceNodes.Enable(node.InstanceId))
            {
                enabled++;
            }
        }

        return enabled;
    }
}
