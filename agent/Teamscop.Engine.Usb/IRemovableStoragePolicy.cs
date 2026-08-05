namespace Teamscop.Engine.Usb;

/// <summary>
/// PC-side block for USB mass storage only (Removable Disks policy).
/// Does not touch HID (mouse/keyboard) classes.
/// </summary>
public interface IRemovableStoragePolicy
{
    bool IsSupported { get; }
    void ApplyBlock();
    void LiftBlock();
    bool IsBlocked { get; }
}
