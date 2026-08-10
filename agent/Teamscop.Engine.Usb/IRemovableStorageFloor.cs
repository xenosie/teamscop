namespace Teamscop.Engine.Usb;

/// <summary>
/// The machine-wide default deny that holds whenever no device is approved.
///
/// It is the belt, not the braces: <see cref="IDeviceGate"/> is what makes a stick not appear at
/// all (§9.2). The floor covers the moment between a never-before-seen device enumerating and the
/// gate disabling it, and it is the whole of the protection on a machine where the gate is
/// unavailable. Nothing here touches HID, so mouse and keyboard are unaffected.
/// </summary>
public interface IRemovableStorageFloor
{
    bool IsSupported { get; }

    /// <summary>True while the deny is in force.</summary>
    bool IsBlocked { get; }

    void ApplyBlock();

    void LiftBlock();
}

/// <summary>
/// Per-device block and allow at the Windows device node. Disabling the disk node removes the
/// volume, so there is no drive letter, nothing in Explorer and nothing in This PC — which is what
/// §9.2 asks for. The device still shows in Device Manager as disabled; making hardware literally
/// invisible needs a signed upper-filter driver, which §15.4 rules out until a certificate exists.
/// </summary>
public interface IDeviceGate
{
    bool IsSupported { get; }

    /// <summary>Disable this device node. Safe to call for a device that is already gone.</summary>
    bool Block(string deviceInstanceId);

    /// <summary>Re-enable one approved device node.</summary>
    bool Allow(string deviceInstanceId);

    bool IsDeviceBlocked(string deviceInstanceId);
}
