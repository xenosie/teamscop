using Teamscop.App.ViewModels;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.Tests;

/// <summary>
/// Defect 3 — "even though one staff registered in business, in the admin side, cannot fetch it …
/// i could not find any item in staffs dropdown."
///
/// The server was innocent: <c>GET /api/tracking/staff</c> returned the new employee the whole
/// time. The desktop simply never asked again. <c>ReloadAsync</c> latches after its first success
/// and, before this fix, cold start was its only non-forced caller — so a roster fetched once at
/// start-up stayed frozen for the life of the window.
/// </summary>
public class StaffDirectoryDiscoveryTests
{
    [Fact]
    public void AStaffMemberWhoRegistersAfterTheWindowOpened_AppearsOnAForcedReload()
        => AppTestHost.Run(async () =>
        {
            var api = new StubApi();
            using var host = TestServices.SignedIn(api);
            var vm = new StaffDirectoryViewModel(host.Services);

            // Cold start on a company with nobody in it — exactly the owner's Tuesday.
            await vm.ReloadAsync(force: false);
            Assert.Empty(vm.Items);

            api.Staff.Add(new OrgStaffDto { UserId = Guid.NewGuid(), Username = "Solara" });

            // This is the defect, stated as an assertion: the un-forced path is latched, so
            // nothing short of a force (or a restart) can ever see the new employee.
            await vm.ReloadAsync(force: false);
            Assert.Empty(vm.Items);

            await vm.ReloadAsync(force: true);
            Assert.Single(vm.Items);
            Assert.Equal("Solara", vm.Items[0].Name);
        });

    [Fact]
    public void AFailedLoad_SaysSo_AndIsNeverRenderedAsAnEmptyCompany()
        => AppTestHost.Run(async () =>
        {
            var api = new StubApi { StaffFailure = new HttpRequestException("connection refused") };
            using var host = TestServices.SignedIn(api);
            var vm = new StaffDirectoryViewModel(host.Services);

            await vm.ReloadAsync(force: true);

            Assert.True(vm.ShowError);
            Assert.NotNull(vm.LoadError);
            Assert.False(
                vm.ShowEmpty,
                "\"No staff yet\" is a statement about the company. A request that failed must "
                + "never be reported as one, or the admin debugs the wrong thing for an hour.");
        });

    [Fact]
    public void Retry_RecoversFromAFailedLoad()
        => AppTestHost.Run(async () =>
        {
            var api = new StubApi { StaffFailure = new HttpRequestException("connection refused") };
            using var host = TestServices.SignedIn(api);
            var vm = new StaffDirectoryViewModel(host.Services);

            await vm.ReloadAsync(force: true);
            Assert.True(vm.ShowError);

            api.StaffFailure = null;
            api.Staff.Add(new OrgStaffDto { UserId = Guid.NewGuid(), Username = "Solara" });
            await vm.RetryCommand.ExecuteAsync(null);

            Assert.Null(vm.LoadError);
            Assert.False(vm.ShowError);
            Assert.Single(vm.Items);
        });

    [Fact]
    public void TheSignedInAdmin_IsNeverInTheirOwnRoster()
        => AppTestHost.Run(async () =>
        {
            var selfId = Guid.NewGuid();
            var api = new StubApi();
            using var host = TestServices.SignedIn(api, role: "admin", userId: selfId);
            var vm = new StaffDirectoryViewModel(host.Services);

            // A server that ever stopped applying §4.5 would hand the admin their own row; defect
            // 8 says the owner must never be presented to himself as a monitored subject.
            api.Staff.Add(new OrgStaffDto { UserId = selfId, Username = "Ocean Group" });
            api.Staff.Add(new OrgStaffDto { UserId = Guid.NewGuid(), Username = "Solara" });

            await vm.ReloadAsync(force: true);

            Assert.Single(vm.Items);
            Assert.Equal("Solara", vm.Items[0].Name);
        });

    [Fact]
    public void OverlappingForcedReloads_LeaveOneCoherentList()
        => AppTestHost.Run(async () =>
        {
            var api = new StubApi();
            api.Staff.Add(new OrgStaffDto { UserId = Guid.NewGuid(), Username = "Solara" });
            using var host = TestServices.SignedIn(api);
            var vm = new StaffDirectoryViewModel(host.Services);

            // Route entry, the nav chevron and the server push can now all fire at once.
            await Task.WhenAll(
                vm.ReloadAsync(force: true),
                vm.ReloadAsync(force: true),
                vm.ReloadAsync(force: true));

            Assert.Single(vm.Items);
            Assert.Equal(3, api.StaffCalls);
        });
}
