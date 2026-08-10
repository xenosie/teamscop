using Teamscop.App.Composition;
using Teamscop.App.Services;

namespace Teamscop.App.Tests;

/// <summary>
/// Defect 2 — "if admin close the app, how to re-open it again? cannot find the item at any place."
///
/// The close semantics were already right for an admin (closing exits, so the shortcut relaunches);
/// what was missing was any recovery that did not depend on a shortcut the installer had made
/// unlaunchable, and any protection against a second launch adding a window instead of raising the
/// one that exists.
/// </summary>
public class ShellRecoveryTests
{
    [Fact]
    public void AnAdminConsole_ExitsOnClose_AndCarriesATrayIconAsWell()
    {
        var plan = ShellPresencePlan.For(stickerActive: false);

        Assert.True(plan.CloseExitsProcess);
        Assert.True(plan.AssignMainWindow);
        Assert.True(plan.ShowTrayIcon);
        Assert.False(plan.StickerRestoresShell);
    }

    [Fact]
    public void AMonitoredMachine_HidesToItsSticker_AndMustNotAutoShowTheWorkspace()
    {
        var plan = ShellPresencePlan.For(stickerActive: true);

        Assert.False(plan.CloseExitsProcess);
        Assert.True(plan.StickerRestoresShell);
        Assert.False(
            plan.AssignMainWindow,
            "assigning MainWindow makes Avalonia show it, which flashed the full workspace onto "
            + "every monitored employee's screen on every boot.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoBranchLeavesTheAppRunningInvisibleAndUnreachable(bool stickerActive)
        => Assert.True(
            ShellPresencePlan.For(stickerActive).HasRecoveryPath,
            "this is the whole of defect 2: whichever way start-up goes, there must be a way back "
            + "to the window.");
}

/// <summary>
/// The other half of defect 2: launching again must raise the shell that is already up. Named
/// mutexes are cross-process AND intra-process, so the second acquisition here is exactly what a
/// second launch sees.
/// </summary>
public class SingleInstanceGuardTests
{
    [Fact]
    public void TheSecondLaunchIsNotPrimary_AndTheNameIsReleasedWhenTheFirstExits()
    {
        var log = new UiLog();
        var name = "Teamscop.Tests." + Guid.NewGuid().ToString("N");

        var first = SingleInstanceGuard.Acquire(name, log);
        Assert.True(first.IsPrimary);

        var second = SingleInstanceGuard.Acquire(name, log);
        Assert.False(second.IsPrimary, "a second launch must not open a second shell");
        second.Dispose();

        first.Dispose();

        // A crash or a clean exit both have to give the name back, or the app becomes unstartable.
        var third = SingleInstanceGuard.Acquire(name, log);
        Assert.True(third.IsPrimary);
        third.Dispose();
    }

    [Fact]
    public void DistinctNamesDoNotCollide()
    {
        var log = new UiLog();
        var a = SingleInstanceGuard.Acquire("Teamscop.Tests." + Guid.NewGuid().ToString("N"), log);
        var b = SingleInstanceGuard.Acquire("Teamscop.Tests." + Guid.NewGuid().ToString("N"), log);

        Assert.True(a.IsPrimary);
        Assert.True(b.IsPrimary);
        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public void TheKernelObjectNamesAreSessionScoped()
    {
        // "Local\" and not "Global\": an admin console and a monitored account signed in at the
        // same time are different desktop sessions and each deserves its own shell.
        Assert.StartsWith("Local\\", SingleInstanceGuard.MutexName(SingleInstanceGuard.DefaultName));
        Assert.StartsWith("Local\\", SingleInstanceGuard.EventName(SingleInstanceGuard.DefaultName));
        Assert.NotEqual(
            SingleInstanceGuard.MutexName(SingleInstanceGuard.DefaultName),
            SingleInstanceGuard.EventName(SingleInstanceGuard.DefaultName));
    }
}
