namespace Teamscop.Api.Tests;

/// <summary>
/// A test that only means anything on Windows, skipped everywhere else with the reason attached.
///
/// The agent is a Windows product (§15.2) and CI is Linux, so several behaviours — DPAPI sealing,
/// the SetupDi device gate, the removable-storage registry floor, real last-input idle time — cannot
/// execute here. Writing the test and skipping it is deliberately different from not writing it: the
/// skip shows up in the run and in the coverage report, so nobody mistakes an unrunnable path for a
/// covered one.
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute(string because)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = $"Windows-only: {because}";
        }
    }
}
