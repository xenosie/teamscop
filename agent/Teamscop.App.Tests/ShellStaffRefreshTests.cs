using Teamscop.App.ViewModels;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.Tests;

/// <summary>
/// Defect 3, the shell half. Fixing <c>StaffDirectoryViewModel</c> is not enough — something has to
/// ask it. Before this, the only non-forced caller was cold start, so the roster was read exactly
/// once per process. These are the three places that now ask again: an authority change, entering
/// the Staff route, and opening the nav chevron the owner calls "the staff dropdown".
/// </summary>
public class ShellStaffRefreshTests
{
    private static EffectiveAuthoritiesDto AdminAuthorities => new()
    {
        UserId = Guid.NewGuid(),
        IsAdmin = true,
        AuthorityVersion = 1
    };

    [Fact]
    public void OpeningTheStaffDropdown_ReReadsTheRoster()
        => AppTestHost.Run(async () =>
        {
            var api = new StubApi();
            using var host = TestServices.SignedIn(api);
            await using var shell = new ShellViewModel(host.Services);

            host.Services.Authority.Apply(AdminAuthorities);
            await AppTestHost.Until(() => api.StaffCalls > 0, "an authority change re-reads staff");
            var afterCapabilities = api.StaffCalls;

            // Someone registers while the admin sits on an open window.
            api.Staff.Add(new OrgStaffDto { UserId = Guid.NewGuid(), Username = "Solara" });

            shell.ToggleStaffListCommand.Execute(null);   // collapse — no request
            Assert.Equal(afterCapabilities, api.StaffCalls);

            shell.ToggleStaffListCommand.Execute(null);   // expand — a request
            await AppTestHost.Until(
                () => api.StaffCalls > afterCapabilities,
                "opening the dropdown is a request to see who is there now");
            await AppTestHost.Until(
                () => shell.StaffDirectory.Items.Count == 1,
                "and the new employee has to actually land in the list");
        });

    [Fact]
    public void EnteringTheStaffRoute_ReReadsTheRoster()
        => AppTestHost.Run(async () =>
        {
            var api = new StubApi();
            using var host = TestServices.SignedIn(api);
            await using var shell = new ShellViewModel(host.Services);

            host.Services.Authority.Apply(AdminAuthorities);
            await AppTestHost.Until(() => api.StaffCalls > 0, "an authority change re-reads staff");
            var before = api.StaffCalls;

            var staffRoute = shell.VisibleRoutes.FirstOrDefault(r => r.Id == ShellRouteId.Staff);
            Assert.NotNull(staffRoute);

            api.Staff.Add(new OrgStaffDto { UserId = Guid.NewGuid(), Username = "Solara" });
            shell.NavigateCommand.Execute(staffRoute);

            await AppTestHost.Until(
                () => api.StaffCalls > before,
                "navigating to Staff must fetch the roster, not render whatever cold start found");
        });

    [Fact]
    public void AnAdminConsoleNeverPutsAMonitoringStickerOnItsOwnScreen()
        => AppTestHost.Run(() =>
        {
            using var host = TestServices.SignedIn(new StubApi(), role: "admin");

            // Defect 8. The sticker is the monitored-machine badge (§4.6); the owner's console is
            // not a monitored machine, and the app must never suggest otherwise.
            Assert.False(host.Services.Sticker.IsActive);
            return Task.CompletedTask;
        });
}
