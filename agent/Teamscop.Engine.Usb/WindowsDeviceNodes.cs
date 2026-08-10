using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Teamscop.Engine.Usb;

/// <summary>One USB mass-storage disk device node, as Windows sees it.</summary>
internal sealed record DeviceNode(
    string InstanceId,
    string? ContainerId,
    string? FriendlyName,
    bool Disabled);

/// <summary>
/// SetupAPI/CfgMgr over the USB mass-storage <b>disk</b> device nodes, and nothing else.
///
/// Safety is the whole design here: this code disables device nodes, and a filter that matched the
/// boot disk would brick the machine. Three independent guards must all hold before a device is
/// touched — the enumerator prefix (<c>USBSTOR\</c> or <c>SCSI\</c>), a removal policy that is not
/// <c>EXPECT_NO_REMOVAL</c>, and — for the <c>SCSI</c> enumerator, which also carries internal
/// controllers — a bus type of USB. A device that fails to answer any of those questions is
/// skipped, never assumed safe.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsDeviceNodes
{
    /// <summary>GUID_DEVCLASS_DISKDRIVE — the setup class whose members own drive letters.</summary>
    private static Guid _diskDriveClass = new("4d36e967-e325-11ce-bfc1-08002be10318");

    /// <summary>GUID_BUS_TYPE_USB.</summary>
    private static readonly Guid UsbBusType = new("9d7debbc-c85d-11d1-9eb4-006008c3a19a");

    /// <summary>
    /// USBSTOR carries BOT flash drives; SCSI carries UAS-mode external drives, which is why the
    /// bus-type check below exists rather than a second enumerator name.
    /// </summary>
    private static readonly string[] Enumerators = ["USBSTOR", "SCSI"];

    private const uint DigcfPresent = 0x02;
    private const uint DifPropertyChange = 0x12;
    private const uint DicsEnable = 0x01;
    private const uint DicsDisable = 0x02;
    private const uint DicsFlagGlobal = 0x01;
    private const uint DicsFlagConfigSpecific = 0x02;

    private const uint SpdrpDeviceDesc = 0x00;
    private const uint SpdrpFriendlyName = 0x0C;
    private const uint SpdrpRemovalPolicy = 0x1F;
    private const uint RemovalPolicyExpectNoRemoval = 1;

    private const uint DnHasProblem = 0x00000400;
    private const uint CmProbDisabled = 22;
    private const int CrSuccess = 0;

    private static readonly IntPtr InvalidHandle = new(-1);

    private static DevPropKey _containerIdKey = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
    private static DevPropKey _busTypeKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 23);

    /// <summary>Every USB mass-storage disk node currently attached, disabled ones included.</summary>
    public static List<DeviceNode> ListPresent()
    {
        var found = new List<DeviceNode>();
        foreach (var enumerator in Enumerators)
        {
            var set = SetupDiGetClassDevsW(ref _diskDriveClass, enumerator, IntPtr.Zero, DigcfPresent);
            if (set == InvalidHandle)
            {
                continue;
            }

            try
            {
                var info = NewDevInfoData();
                for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref info); index++)
                {
                    var instanceId = GetInstanceId(set, ref info);
                    if (instanceId is null || !IsGateable(set, ref info, instanceId, enumerator))
                    {
                        continue;
                    }

                    found.Add(new DeviceNode(
                        instanceId,
                        GetContainerId(set, ref info),
                        GetFriendlyName(set, ref info),
                        IsDisabled(info.DevInst)));
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
        }

        return found;
    }

    /// <summary>Disables the device node, so Windows presents no volume and no drive letter (§9.2).</summary>
    public static bool Disable(string instanceId)
        => ChangeState(instanceId, DicsDisable, [DicsFlagConfigSpecific]);

    /// <summary>
    /// Re-enables one approved device. Both scopes are attempted because a node can be disabled in
    /// either — this mirrors what devcon does and is the reason a single-scope enable silently
    /// "succeeds" while the device stays dark.
    /// </summary>
    public static bool Enable(string instanceId)
        => ChangeState(instanceId, DicsEnable, [DicsFlagGlobal, DicsFlagConfigSpecific]);

    public static bool IsNodeDisabled(string instanceId)
    {
        var result = false;
        WithNode(instanceId, (IntPtr _, ref SP_DEVINFO_DATA info) =>
        {
            result = IsDisabled(info.DevInst);
            return true;
        });
        return result;
    }

    private static bool ChangeState(string instanceId, uint stateChange, uint[] scopes)
        => WithNode(instanceId, (IntPtr set, ref SP_DEVINFO_DATA info) =>
        {
            var applied = false;
            foreach (var scope in scopes)
            {
                var parameters = new SP_PROPCHANGE_PARAMS
                {
                    ClassInstallHeader = new SP_CLASSINSTALL_HEADER
                    {
                        cbSize = Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
                        InstallFunction = DifPropertyChange
                    },
                    StateChange = stateChange,
                    Scope = scope,
                    HwProfile = 0
                };

                if (SetupDiSetClassInstallParamsW(set, ref info, ref parameters, Marshal.SizeOf<SP_PROPCHANGE_PARAMS>())
                    && SetupDiCallClassInstaller(DifPropertyChange, set, ref info))
                {
                    applied = true;
                }
            }

            return applied;
        });

    private delegate bool NodeAction(IntPtr set, ref SP_DEVINFO_DATA info);

    /// <summary>
    /// Finds one node by instance id and runs <paramref name="action"/> on it. Absent devices are
    /// searched too (no DIGCF_PRESENT), so a stick removed while approved can be re-disabled while
    /// it is gone — <c>DICS_FLAG_CONFIGSPECIFIC</c> persists that state, which is what makes the
    /// next insert blocked before enumeration finishes.
    /// </summary>
    private static bool WithNode(string instanceId, NodeAction action)
    {
        foreach (var enumerator in Enumerators)
        {
            foreach (var flags in new[] { DigcfPresent, 0u })
            {
                var set = SetupDiGetClassDevsW(ref _diskDriveClass, enumerator, IntPtr.Zero, flags);
                if (set == InvalidHandle)
                {
                    continue;
                }

                try
                {
                    var info = NewDevInfoData();
                    for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref info); index++)
                    {
                        var found = GetInstanceId(set, ref info);
                        if (found is null
                            || !string.Equals(found, instanceId, StringComparison.OrdinalIgnoreCase)
                            || !IsGateable(set, ref info, found, enumerator))
                        {
                            continue;
                        }

                        return action(set, ref info);
                    }
                }
                finally
                {
                    SetupDiDestroyDeviceInfoList(set);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The safety gate. Every condition is a reason to refuse, never a reason to assume.
    /// </summary>
    private static bool IsGateable(IntPtr set, ref SP_DEVINFO_DATA info, string instanceId, string enumerator)
    {
        if (!instanceId.StartsWith(enumerator + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A fixed internal disk reports EXPECT_NO_REMOVAL. Never touch one.
        var removal = GetDwordProperty(set, ref info, SpdrpRemovalPolicy);
        if (removal is null || removal == RemovalPolicyExpectNoRemoval)
        {
            return false;
        }

        if (string.Equals(enumerator, "USBSTOR", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // SCSI also enumerates internal controllers; only a USB bus type earns the gate.
        return GetGuidProperty(set, ref info, ref _busTypeKey) == UsbBusType;
    }

    private static bool IsDisabled(uint devInst)
        => CM_Get_DevNode_Status(out var status, out var problem, devInst, 0) == CrSuccess
           && (status & DnHasProblem) != 0
           && problem == CmProbDisabled;

    private static SP_DEVINFO_DATA NewDevInfoData()
        => new() { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };

    private static string? GetInstanceId(IntPtr set, ref SP_DEVINFO_DATA info)
    {
        var buffer = new char[512];
        if (!SetupDiGetDeviceInstanceIdW(set, ref info, buffer, (uint)buffer.Length, out _))
        {
            return null;
        }

        var end = Array.IndexOf(buffer, '\0');
        return end <= 0 ? null : new string(buffer, 0, end);
    }

    private static string? GetFriendlyName(IntPtr set, ref SP_DEVINFO_DATA info)
        => GetStringProperty(set, ref info, SpdrpFriendlyName)
           ?? GetStringProperty(set, ref info, SpdrpDeviceDesc);

    private static string? GetStringProperty(IntPtr set, ref SP_DEVINFO_DATA info, uint property)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out _, buffer, (uint)buffer.Length, out var size)
            || size < 2)
        {
            return null;
        }

        var text = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, buffer.Length)).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static uint? GetDwordProperty(IntPtr set, ref SP_DEVINFO_DATA info, uint property)
    {
        var buffer = new byte[4];
        return SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out _, buffer, 4, out var size) && size == 4
            ? BitConverter.ToUInt32(buffer)
            : null;
    }

    private static Guid? GetGuidProperty(IntPtr set, ref SP_DEVINFO_DATA info, ref DevPropKey key)
    {
        var buffer = new byte[16];
        return SetupDiGetDevicePropertyW(set, ref info, ref key, out _, buffer, buffer.Length, out var size, 0)
               && size == 16
            ? new Guid(buffer)
            : null;
    }

    private static string? GetContainerId(IntPtr set, ref SP_DEVINFO_DATA info)
    {
        var value = GetGuidProperty(set, ref info, ref _containerIdKey);
        return value is null || value == Guid.Empty ? null : value.Value.ToString("B");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_CLASSINSTALL_HEADER
    {
        public int cbSize;
        public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_PROPCHANGE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA info);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA info,
        [Out] char[] instanceId,
        uint instanceIdSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA info,
        uint property,
        out uint propertyRegDataType,
        [Out] byte[] propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA info,
        ref DevPropKey propertyKey,
        out ulong propertyType,
        [Out] byte[] propertyBuffer,
        int propertyBufferSize,
        out int requiredSize,
        uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiSetClassInstallParamsW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA info,
        ref SP_PROPCHANGE_PARAMS classInstallParams,
        int classInstallParamsSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiCallClassInstaller(
        uint installFunction,
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA info);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);
}
