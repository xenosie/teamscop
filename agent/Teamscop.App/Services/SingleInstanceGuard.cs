using Teamscop.App.Composition;

namespace Teamscop.App.Services;

/// <summary>
/// Defect 2 — one shell per desktop session, and clicking the shortcut again brings THAT shell
/// forward instead of doing nothing.
///
/// Without this, a second launch either raced the first over the same state file or (once the
/// shortcut worked again) started a window the user could not tell apart from the one they already
/// had. The guard is deliberately fail-open: if the OS refuses the primitives, start-up proceeds
/// as primary rather than refusing to run at all — a monitoring product that will not start is a
/// worse failure than two windows.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>
    /// Session-scoped, not machine-scoped. Two users signed in at once (fast user switching, or an
    /// admin console beside a monitored account) each get their own shell, which is correct: the
    /// state stores are per-user too.
    /// </summary>
    public const string DefaultName = "Teamscop.App.Shell";

    private readonly Mutex? _mutex;
    private readonly string _name;
    private EventWaitHandle? _activation;
    private Thread? _listener;
    private volatile bool _disposed;

    private SingleInstanceGuard(Mutex? mutex, string name, bool isPrimary)
    {
        _mutex = mutex;
        _name = name;
        IsPrimary = isPrimary;
    }

    /// <summary>Another instance asked to be seen. Raised off the UI thread.</summary>
    public event Action? ActivationRequested;

    /// <summary>False only when a live instance already owns this name — then do not open a window.</summary>
    public bool IsPrimary { get; }

    /// <summary>Named kernel objects need a namespace prefix on Windows; Unix ignores it.</summary>
    public static string MutexName(string name) => $"Local\\{name}.mutex";

    public static string EventName(string name) => $"Local\\{name}.activate";

    public static SingleInstanceGuard Acquire(string name, UiLog log)
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, MutexName(name), out var createdNew);
            if (createdNew)
            {
                return new SingleInstanceGuard(mutex, name, isPrimary: true);
            }

            // Somebody else owns the name. Release our handle immediately — holding it would keep
            // the object alive past our exit and make the NEXT launch look like a third instance.
            mutex.Dispose();
            return new SingleInstanceGuard(null, name, isPrimary: false);
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException
                                       or IOException or NotSupportedException or PlatformNotSupportedException)
        {
            log.Debug($"Single-instance guard unavailable ({ex.GetType().Name}) — starting anyway");
            return new SingleInstanceGuard(null, name, isPrimary: true);
        }
    }

    /// <summary>
    /// Primary only: start listening for a later launch. The caller reveals its window when the
    /// event fires, which is the whole reason the second process is allowed to exit quietly.
    /// </summary>
    public void ListenForActivation(UiLog log)
    {
        if (!IsPrimary || !OperatingSystem.IsWindows() || _listener is not null)
        {
            // Named events are Windows-only in .NET. Elsewhere the mutex still prevents a second
            // shell; the user relaunches from the shortcut, which is the documented path anyway.
            return;
        }

        try
        {
            _activation = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(_name));
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException
                                       or IOException or PlatformNotSupportedException)
        {
            log.Debug($"Activation channel unavailable ({ex.GetType().Name})");
            return;
        }

        _listener = new Thread(WaitLoop) { IsBackground = true, Name = "TeamscopActivation" };
        _listener.Start();
    }

    /// <summary>
    /// Secondary only: ask the running instance to show itself, then exit. False when the request
    /// could not be delivered, in which case the caller should say so rather than vanish silently.
    /// </summary>
    public bool SignalPrimary()
    {
        if (IsPrimary || !OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var handle = EventWaitHandle.OpenExisting(EventName(_name));
            handle.Set();
            return true;
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException
                                       or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            _activation?.Set();
        }
        catch (ObjectDisposedException)
        {
            // Racing our own shutdown; the loop is exiting regardless.
        }

        _activation?.Dispose();
        _activation = null;
        _mutex?.Dispose();
    }

    private void WaitLoop()
    {
        var handle = _activation;
        while (!_disposed && handle is not null)
        {
            try
            {
                if (!handle.WaitOne())
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or AbandonedMutexException)
            {
                return;
            }

            if (_disposed)
            {
                return;
            }

            ActivationRequested?.Invoke();
        }
    }
}
