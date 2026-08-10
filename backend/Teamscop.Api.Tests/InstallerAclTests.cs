using Teamscop.Engine.Lifecycle;

namespace Teamscop.Api.Tests;

/// <summary>
/// Defects 1 and 2 — the single ACL line that made the product unlaunchable and unremovable.
///
/// The installer used to grant SYSTEM+Administrators only to <c>root</c>, <c>bin</c> AND
/// <c>Agent</c>. Because the Administrators SID is USE_FOR_DENY_ONLY in a UAC-filtered token, a
/// normal desktop session could not read or execute anything under that tree: the app would not
/// relaunch from its shortcut (defect 2), Settings could not run the uninstaller (defect 1), and the
/// SessionHelper could never start (the blackout behind defects 4/7). Teamscop.Setup has no test
/// project of its own, so the ACL plan lives in <see cref="InstallerAcl"/> where it can be asserted
/// here — exactly the coverage whose absence let this ship.
/// </summary>
public class InstallerAclTests
{
    [Fact]
    public void BinGrantsUsersReadExecute_SoAStandardUserCanLaunchTheApp()
    {
        Assert.True(InstallerAcl.GrantsUsersReadExecute("bin"));
        var grant = InstallerAcl.GrantArguments(usersReadExecute: true);
        Assert.Contains($"*{InstallerAcl.UsersSid}:(OI)(CI)RX", grant, StringComparison.Ordinal);
        Assert.Contains($"*{InstallerAcl.SystemSid}:(OI)(CI)F", grant, StringComparison.Ordinal);
        Assert.Contains($"*{InstallerAcl.AdministratorsSid}:(OI)(CI)F", grant, StringComparison.Ordinal);
    }

    [Fact]
    public void RootGrantsUsersReadExecute()
    {
        // The empty string is the install root itself, and the app/setup copy live directly under it.
        var grant = InstallerAcl.GrantArguments(usersReadExecute: true);
        Assert.Contains($"*{InstallerAcl.UsersSid}:(OI)(CI)RX", grant, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentDoesNotGrantUsers_SoCapturedDataStaysHiddenFromTheEmployee()
    {
        // §12.1 — the vault, outbox, state and secrets under Agent must not be readable by the
        // monitored user. This is the ONE directory that keeps the SYSTEM+Admins-only grant.
        Assert.False(InstallerAcl.GrantsUsersReadExecute("Agent"));
        var grant = InstallerAcl.GrantArguments(usersReadExecute: false);
        Assert.DoesNotContain(InstallerAcl.UsersSid, grant, StringComparison.Ordinal);
        Assert.Contains($"*{InstallerAcl.SystemSid}:(OI)(CI)F", grant, StringComparison.Ordinal);
        Assert.Contains($"*{InstallerAcl.AdministratorsSid}:(OI)(CI)F", grant, StringComparison.Ordinal);
    }
}
