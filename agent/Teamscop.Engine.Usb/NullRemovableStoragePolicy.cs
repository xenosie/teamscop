namespace Teamscop.Engine.Usb;

/// <summary>No-op floor for non-Windows / CI hosts. Records state so tests can assert on it.</summary>
public sealed class NullRemovableStoragePolicy : IRemovableStorageFloor
{
    private bool _blocked;

    public bool IsSupported => false;
    public bool IsBlocked => _blocked;

    public void ApplyBlock() => _blocked = true;
    public void LiftBlock() => _blocked = false;
}

/// <summary>
/// No-op gate for non-Windows / CI hosts. It remembers which devices were blocked so the session
/// state machine is exercisable off Windows, but it hides nothing.
/// </summary>
public sealed class NullDeviceGate : IDeviceGate
{
    private readonly HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);

    public bool IsSupported => false;

    public bool Block(string deviceInstanceId)
    {
        lock (_blocked)
        {
            _blocked.Add(deviceInstanceId);
        }

        return true;
    }

    public bool Allow(string deviceInstanceId)
    {
        lock (_blocked)
        {
            _blocked.Remove(deviceInstanceId);
        }

        return true;
    }

    public bool IsDeviceBlocked(string deviceInstanceId)
    {
        lock (_blocked)
        {
            return _blocked.Contains(deviceInstanceId);
        }
    }
}
