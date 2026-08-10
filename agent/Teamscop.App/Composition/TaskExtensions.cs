namespace Teamscop.App.Composition;

/// <summary>
/// The one sanctioned way to start work nobody awaits. A bare <c>_ = SomethingAsync()</c> loses
/// its exception entirely; this records it instead.
/// </summary>
public static class TaskExtensions
{
    public static void FireAndForget(this Task task, UiLog log, string context)
    {
        _ = task.ContinueWith(
            t => log.Warn($"{context} failed in the background", t.Exception?.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
