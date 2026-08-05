namespace Teamscop.Engine.Usb;

/// <summary>No-op policy for non-Windows / CI hosts.</summary>
public sealed class NullRemovableStoragePolicy : IRemovableStoragePolicy
{
    private bool _blocked;

    public bool IsSupported => false;
    public bool IsBlocked => _blocked;

    public void ApplyBlock() => _blocked = true;
    public void LiftBlock() => _blocked = false;
}
