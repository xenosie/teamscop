using Avalonia.Media;
using Teamscop.App.Composition;
using Teamscop.App.ViewModels;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.Tests;

/// <summary>
/// §5.1 — a circle badge on each staff row: green working, red rest, grey offline. The state comes
/// from the backend presence field and is merged onto each row by user id, refreshing while the
/// roster is visible.
/// </summary>
public class StaffPresenceBadgeTests
{
    [Fact]
    public void Presence_IsMergedOntoEachRow_ByUserId()
        => AppTestHost.Run(async () =>
        {
            var working = Guid.NewGuid();
            var resting = Guid.NewGuid();
            var away = Guid.NewGuid();
            var api = new StubApi();
            api.Staff.Add(new OrgStaffDto { UserId = working, Username = "Worker" });
            api.Staff.Add(new OrgStaffDto { UserId = resting, Username = "Rester" });
            api.Staff.Add(new OrgStaffDto { UserId = away, Username = "Away" });
            api.Presence.Add(new StaffPresence(working, StaffPresenceState.Working));
            api.Presence.Add(new StaffPresence(resting, StaffPresenceState.Rest));
            // 'away' is deliberately not named by the server → it must read as offline (grey).

            using var host = TestServices.SignedIn(api);
            var vm = new StaffDirectoryViewModel(host.Services);

            await vm.ReloadAsync(force: true);
            await vm.RefreshPresenceAsync();

            var rows = vm.Items.ToDictionary(i => i.UserId);
            Assert.Equal(StaffPresenceState.Working, rows[working].Presence);
            Assert.Equal(StaffPresenceState.Rest, rows[resting].Presence);
            Assert.Equal(StaffPresenceState.Offline, rows[away].Presence);

            Assert.True(rows[working].HasPresence);
            Assert.Equal("#FF16A34A", ((SolidColorBrush)rows[working].StatusBrush).Color.ToString());
            Assert.Equal("#FFDC2626", ((SolidColorBrush)rows[resting].StatusBrush).Color.ToString());
            Assert.Equal("#FF94A3B8", ((SolidColorBrush)rows[away].StatusBrush).Color.ToString());
        });

    [Fact]
    public void BeforePresenceIsKnown_NoBadgeIsDrawn()
        => AppTestHost.Run(async () =>
        {
            var api = new StubApi();
            api.Staff.Add(new OrgStaffDto { UserId = Guid.NewGuid(), Username = "Solara" });
            using var host = TestServices.SignedIn(api);
            var vm = new StaffDirectoryViewModel(host.Services);

            await vm.ReloadAsync(force: true);

            // No presence fetched yet: the badge is Unknown and stays hidden, never a guessed colour.
            Assert.Equal(StaffPresenceState.Unknown, vm.Items[0].Presence);
            Assert.False(vm.Items[0].HasPresence);
        });

    [Fact]
    public void StartPresence_BadgesTheRosterAlreadyOnScreen()
        => AppTestHost.Run(async () =>
        {
            var id = Guid.NewGuid();
            var api = new StubApi();
            api.Staff.Add(new OrgStaffDto { UserId = id, Username = "Solara" });
            api.Presence.Add(new StaffPresence(id, StaffPresenceState.Working));
            using var host = TestServices.SignedIn(api);
            var vm = new StaffDirectoryViewModel(host.Services);
            await vm.ReloadAsync(force: true);

            vm.StartPresence();

            await AppTestHost.Until(
                () => vm.Items[0].Presence == StaffPresenceState.Working,
                "starting presence badges a roster that is already loaded");
            Assert.True(api.PresenceCalls >= 1);
            vm.StopPresence();
        });
}
