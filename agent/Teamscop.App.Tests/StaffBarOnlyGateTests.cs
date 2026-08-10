using Teamscop.App.ViewModels;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.Tests;

/// <summary>
/// §7.1 / §7.2 — plain staff (not leader, not policeman) can never open the main window; they get
/// the bar only. The enrolment/admin case, where the shell is the whole UI, must always be able to
/// open it, and a promotion to a workspace role must restore shell access mid-session. The gate is
/// one predicate on <see cref="StickerHost"/> that every open path funnels through.
/// </summary>
public class StaffBarOnlyGateTests
{
    [Fact]
    public void OnAMonitoredMachine_TheShellIsGatedShutUntilAWorkspaceArrives()
        => AppTestHost.Run(() =>
        {
            using var host = TestServices.SignedIn(new StubApi(), role: "staff", userId: Guid.NewGuid());
            var sticker = host.Services.Sticker;

            // Monitored + registered: the sticker owns the screen (§4.6).
            Assert.True(sticker.IsActive);

            // Plain staff: no workspace has been fed, so the shell cannot open — bar only (§7.1).
            Assert.False(sticker.CanOpenShell);

            // A promotion to leader/policeman feeds a workspace and opens the gate (§7.2).
            sticker.SetHasWorkspace(true);
            Assert.True(sticker.CanOpenShell);

            // A demotion closes it again, with no restart.
            sticker.SetHasWorkspace(false);
            Assert.False(sticker.CanOpenShell);
            return Task.CompletedTask;
        });

    [Fact]
    public void WhereTheShellIsTheWholeUI_ItCanAlwaysOpen()
        => AppTestHost.Run(() =>
        {
            // An admin console is not a monitored machine (§7.2): the shell hosts everything, so it
            // must open regardless of the workspace flag. The same holds for a not-yet-joined
            // machine, which is IsActive == false for the same reason (no staff session yet).
            using var host = TestServices.SignedIn(new StubApi(), role: "admin");
            var sticker = host.Services.Sticker;

            Assert.False(sticker.IsActive);
            Assert.True(sticker.CanOpenShell);
            sticker.SetHasWorkspace(false);
            Assert.True(sticker.CanOpenShell);
            return Task.CompletedTask;
        });

    [Fact]
    public void HasWorkspace_IsTrueForRolesWithAWorkspace_AndFalseForPlainStaff()
    {
        Assert.False(Capabilities([]).HasWorkspace);
        Assert.True(Capabilities([AuthorityPackageIds.ViewTimeTrack]).HasWorkspace);
        Assert.True(Capabilities([AuthorityPackageIds.TeamManagement]).HasWorkspace);
        Assert.True(Capabilities([AuthorityPackageIds.UsbApproval]).HasWorkspace);
        Assert.True(Capabilities([], isAdmin: true).HasWorkspace);
    }

    [Fact]
    public void TheShellLandsOnLeaderboard_NotTheDeletedTodayPage()
        => AppTestHost.Run(async () =>
        {
            using var host = TestServices.SignedIn(new StubApi(), role: "admin");
            await using var shell = new ShellViewModel(host.Services);

            host.Services.Authority.Apply(new EffectiveAuthoritiesDto
            {
                UserId = Guid.NewGuid(),
                IsAdmin = true,
                AuthorityVersion = 1
            });

            await AppTestHost.Until(() => shell.CurrentRoute is not null, "routes build once authorities land");
            Assert.Equal(ShellRouteId.Leaderboard, shell.CurrentRoute!.Id);
            Assert.DoesNotContain(shell.VisibleRoutes, r => r.Label.Contains("Today", StringComparison.OrdinalIgnoreCase));
        });

    private static ShellCapabilities Capabilities(string[] packages, bool isAdmin = false)
        => ShellCapabilities.From(
            new EffectiveAuthoritiesDto
            {
                UserId = Guid.NewGuid(),
                IsAdmin = isAdmin,
                Packages = packages.ToList(),
                AuthorityVersion = 1
            },
            placement: null);
}
