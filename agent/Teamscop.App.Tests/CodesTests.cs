using Teamscop.App.ViewModels;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.Tests;

/// <summary>
/// §1.4 / §1.5 / §6 — the Codes page after the derived-credential rebuild. There is no enrolment
/// step and no stored secret: a code is always available for any staff member, and the admin's own
/// machine is never listed. This replaces the old enrol/re-enrol flow, which the derived model deletes.
/// </summary>
public class CodesTests
{
    [Fact]
    public void SelectingAStaffMember_ShowsTheCurrentCode_WithNoEnrolmentStep()
        => AppTestHost.Run(async () =>
        {
            var api = new StubApi();
            api.TotpRows.Add(Row(Guid.NewGuid(), "Solara"));
            using var host = TestServices.SignedIn(api);
            var vm = new TotpCodesViewModel(host.Services);

            await vm.LoadAsync();
            vm.SelectedStaff = vm.StaffRows.Single();

            await AppTestHost.Until(() => vm.HasCode, "selecting a staff member must show a code straight away");
            Assert.Equal("266355", vm.Code);
            Assert.Equal(TotpGenerator.PurposeUsb, Assert.Single(api.CodeRequests));
        });

    [Fact]
    public void SwitchingPurpose_RefetchesTheCodeForThatPurpose()
        => AppTestHost.Run(async () =>
        {
            var api = new StubApi();
            api.TotpRows.Add(Row(Guid.NewGuid(), "Solara"));
            using var host = TestServices.SignedIn(api);
            var vm = new TotpCodesViewModel(host.Services);
            await vm.LoadAsync();
            vm.SelectedStaff = vm.StaffRows.Single();
            await AppTestHost.Until(() => api.CodeRequests.Count == 1, "the first selection fetches a code");

            vm.SelectPurposeCommand.Execute("uninstall");

            await AppTestHost.Until(
                () => api.CodeRequests.Contains(TotpGenerator.PurposeUninstall),
                "switching purpose must request a code for the new purpose");
            Assert.True(vm.IsUninstallPurpose);
        });

    [Fact]
    public void TheStaffList_IsExactlyWhatTheServerReturns_WithNoSelfOrAdminRow()
        => AppTestHost.Run(async () =>
        {
            // §1.5 — the server returns staff only (no admin self-row). The page has no self-machine
            // concept at all; every listed row is a plain staff member with a code.
            var api = new StubApi();
            api.TotpRows.Add(Row(Guid.NewGuid(), "Alpha"));
            api.TotpRows.Add(Row(Guid.NewGuid(), "Bravo"));
            using var host = TestServices.SignedIn(api);
            var vm = new TotpCodesViewModel(host.Services);

            await vm.LoadAsync();

            Assert.Equal(2, vm.StaffRows.Count);
            Assert.Equal(["Alpha", "Bravo"], vm.StaffRows.Select(r => r.DisplayName));
        });

    [Fact]
    public void MovingToAnotherRow_DropsTheStaleCode_ThenFetchesTheNewOne()
        => AppTestHost.Run(async () =>
        {
            var api = new StubApi();
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            api.TotpRows.Add(Row(first, "Alpha"));
            api.TotpRows.Add(Row(second, "Bravo"));
            using var host = TestServices.SignedIn(api);
            var vm = new TotpCodesViewModel(host.Services);
            await vm.LoadAsync();

            vm.SelectedStaff = vm.StaffRows.First(r => r.StaffUserId == first);
            await AppTestHost.Until(() => vm.HasCode, "the first row shows its code");

            vm.SelectedStaff = vm.StaffRows.First(r => r.StaffUserId == second);
            await AppTestHost.Until(() => api.CodeRequests.Count == 2, "moving rows fetches the new member's code");
            Assert.True(vm.StaffRows.First(r => r.StaffUserId == second).IsSelected);
            Assert.False(vm.StaffRows.First(r => r.StaffUserId == first).IsSelected);
        });

    private static TotpStatusResult Row(Guid id, string username) => new()
    {
        StaffUserId = id,
        StaffUsername = username,
        Enabled = true,
        Role = "staff",
        IsSelfMachine = false
    };
}
