using Teamscop.Engine.Lifecycle;
using Teamscop.StaffService;

namespace Teamscop.Api.Tests;

/// <summary>
/// Defect 8, agent half — "admin should never be tracked."
///
/// The installer puts the capturing service on every machine (role is chosen later in the app), so
/// the service itself must refuse to capture, arm USB, or launch the helper unless this is an
/// enrolled STAFF machine. The account-level half of that gate is asserted here; the machine-marker
/// half is Windows-only registry and covered by a WindowsOnlyFact.
/// </summary>
public class AgentCaptureGateTests
{
    [Fact]
    public void NoToken_MeansNoCapture()
    {
        Assert.False(StaffAgentWorker.HasStaffEnrollment(new LocalAgentState()));
        Assert.False(StaffAgentWorker.HasStaffEnrollment(new LocalAgentState { AccessToken = "   " }));
    }

    [Fact]
    public void AdminRole_NeverCaptures_EvenWithAToken()
    {
        // The one case the owner reported: an admin machine must not collect. Even if the staff
        // store somehow held a token, an admin role is excluded.
        Assert.False(StaffAgentWorker.HasStaffEnrollment(new LocalAgentState
        {
            AccessToken = "jwt",
            Role = "admin"
        }));
        Assert.False(StaffAgentWorker.HasStaffEnrollment(new LocalAgentState
        {
            AccessToken = "jwt",
            Role = "Admin"
        }));
    }

    [Fact]
    public void EnrolledStaff_Captures()
    {
        Assert.True(StaffAgentWorker.HasStaffEnrollment(new LocalAgentState
        {
            AccessToken = "jwt",
            Role = "staff"
        }));

        // Role null but token present is treated as staff (the default the heartbeat also uses).
        Assert.True(StaffAgentWorker.HasStaffEnrollment(new LocalAgentState { AccessToken = "jwt" }));
    }

    [Fact]
    public void CaptureEnabledFor_AgreesWithEnrollment_WhenNoAdminMarker()
    {
        // Off Windows InstallRoleMarker.IsAdminMachine() is false, so the composite gate reduces to
        // the enrollment predicate. (On an admin machine the marker would force it false regardless.)
        Assert.True(StaffAgentWorker.CaptureEnabledFor(new LocalAgentState { AccessToken = "jwt", Role = "staff" }));
        Assert.False(StaffAgentWorker.CaptureEnabledFor(new LocalAgentState()));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("Admin")]
    [InlineData("  admin  ")]
    public void ShouldSelfRemove_OnlyWhenTheMarkerSaysAdmin(string marker)
        => Assert.True(StaffAgentWorker.ShouldSelfRemove(marker));

    [Theory]
    [InlineData("staff")]
    [InlineData("unassigned")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ShouldSelfRemove_IsFalse_ForAnythingButAdmin(string? marker)
    {
        // §8.2 — only a positive "admin" removes the service. "unassigned" (the installer's seed) and
        // "staff" must keep it; absence must never be read as admin (that would delete every install).
        Assert.False(StaffAgentWorker.ShouldSelfRemove(marker));
    }
}
