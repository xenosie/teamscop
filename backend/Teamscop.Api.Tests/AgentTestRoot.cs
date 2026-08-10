namespace Teamscop.Api.Tests;

/// <summary>
/// A throwaway agent data directory, deleted when the test finishes.
///
/// Every agent-side component under test (<c>FileOutboxQueue</c>, <c>SecureVault</c>,
/// <c>LocalApprovalVerifier</c>, <c>ChromeHistoryWatcher</c>) is rooted at a directory, which is
/// the seam that already exists — none of them needed one added. Each instance gets its own GUID
/// directory so the classes can run in parallel without sharing state.
/// </summary>
public sealed class AgentTestRoot : IDisposable
{
    public AgentTestRoot(string prefix = "teamscop-test")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts)
        => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a green test over.
        }
    }
}

/// <summary>
/// A clock the test drives by hand. Nothing in these tests waits on wall time: the windows under
/// test are 60 seconds (timetrack flush), 90 seconds (helper liveness), 5 minutes (tracking health)
/// and 15 minutes (approval lockout), and sleeping for any of them would make the suite both slow
/// and load-sensitive.
/// </summary>
public sealed class TestClock(DateTimeOffset start)
{
    private DateTimeOffset _now = start;

    public DateTimeOffset Now => _now;

    public Func<DateTimeOffset> Func => () => _now;

    public void Advance(TimeSpan by) => _now += by;
}
