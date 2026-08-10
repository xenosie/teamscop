using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Teamscop.Engine.Usb;

/// <summary>
/// Windows Removable Storage Access policy for Removable Disks only.
/// Class GUID {53f5630d-b6bf-11d0-94f2-00a0c91efb8b} — HID mouse/keyboard unaffected.
///
/// This alone does NOT satisfy §9.2: the volume still mounts and shows a drive letter, it is only
/// unreadable. <see cref="WindowsDeviceGate"/> is what makes the device not appear. The floor's job
/// is to cover the fraction of a second between a never-before-seen stick enumerating and the gate
/// disabling it — during which the drive is visible but cannot be read or written, so the §9.1
/// threat (data theft) is still denied — and to be the whole of the defence on a machine where
/// SetupAPI is unavailable.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRemovableStorageFloor : IRemovableStorageFloor
{
    private const string RemovableDisksGuid = "{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}";
    private const string PolicyRoot =
        @"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\" + RemovableDisksGuid;

    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public bool IsBlocked
    {
        get
        {
            if (!IsSupported)
            {
                return false;
            }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(PolicyRoot, writable: false);
                if (key is null)
                {
                    return false;
                }

                var denyRead = Convert.ToInt32(key.GetValue("Deny_Read", 0) ?? 0);
                var denyWrite = Convert.ToInt32(key.GetValue("Deny_Write", 0) ?? 0);
                return denyRead == 1 && denyWrite == 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                return false;
            }
        }
    }

    public void ApplyBlock()
    {
        if (!IsSupported)
        {
            return;
        }

        using var key = Registry.LocalMachine.CreateSubKey(PolicyRoot, writable: true)
            ?? throw new InvalidOperationException("Unable to open RemovableStorageDevices policy key (need admin).");
        key.SetValue("Deny_Read", 1, RegistryValueKind.DWord);
        key.SetValue("Deny_Write", 1, RegistryValueKind.DWord);
        key.SetValue("Deny_Execute", 1, RegistryValueKind.DWord);
    }

    public void LiftBlock()
    {
        if (!IsSupported)
        {
            return;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyRoot, writable: true);
            if (key is null)
            {
                return;
            }

            key.SetValue("Deny_Read", 0, RegistryValueKind.DWord);
            key.SetValue("Deny_Write", 0, RegistryValueKind.DWord);
            key.SetValue("Deny_Execute", 0, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Key missing means nothing to lift.
        }
    }
}
