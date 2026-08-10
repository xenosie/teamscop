using Teamscop.App.Services;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.Tests;

/// <summary>
/// §8.2 — a machine repurposed from the admin's desk to an employee must start tracking again.
///
/// The admin path deletes the staff service on purpose: an admin PC is never monitored. Nothing put
/// it back. A PC that was the owner's console and is later joined as staff would show a healthy
/// enrolment, a green sticker and an entry in the staff list, while capturing absolutely nothing —
/// the same silent-blackout shape as the session-0 bug, and just as hard to notice from the admin
/// side. The transition itself is the trigger, so it is what these lock down.
/// </summary>
public class AdminStaffFlipTests
{
    [Theory]
    [InlineData("admin", true)]
    [InlineData("Admin", true)]
    [InlineData("  admin  ", true)]
    [InlineData("staff", false)]
    [InlineData("unassigned", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyAPreviousAdminMarkerCountsAsAnAdminMachine(string? marker, bool isAdmin)
        => Assert.Equal(isAdmin, InstalledRoleWriter.IsAdminRoleValue(marker));

    /// <summary>
    /// The stamped value decides everything downstream, so "admin" must win whenever the two halves
    /// disagree. A staff-store session carrying an admin account is the exact case that used to put
    /// a monitoring sticker on the owner's own screen.
    /// </summary>
    [Theory]
    [InlineData(AgentRole.Admin, "admin", InstalledRoleWriter.Admin)]
    [InlineData(AgentRole.Admin, "staff", InstalledRoleWriter.Admin)]
    [InlineData(AgentRole.Staff, "admin", InstalledRoleWriter.Admin)]
    [InlineData(AgentRole.Staff, "staff", InstalledRoleWriter.Staff)]
    [InlineData(AgentRole.Staff, null, InstalledRoleWriter.Staff)]
    public void AdminWinsWhereverTheStoreAndTheAccountDisagree(
        AgentRole store, string? accountRole, string expected)
        => Assert.Equal(expected, InstalledRoleWriter.RoleFor(store, accountRole));

    /// <summary>
    /// The reinstatement trigger, stated as the rule the writer applies: previous marker was admin
    /// AND the new one is staff. Any other transition must NOT spawn an elevated installer run —
    /// a staff->staff sign-in happens on every launch, and prompting for elevation each time would
    /// be both wrong and intolerable.
    /// </summary>
    [Theory]
    [InlineData("admin", InstalledRoleWriter.Staff, true)]
    [InlineData("Admin", InstalledRoleWriter.Staff, true)]
    [InlineData("staff", InstalledRoleWriter.Staff, false)]
    [InlineData(null, InstalledRoleWriter.Staff, false)]
    [InlineData("unassigned", InstalledRoleWriter.Staff, false)]
    [InlineData("admin", InstalledRoleWriter.Admin, false)]
    [InlineData("staff", InstalledRoleWriter.Admin, false)]
    public void ServiceIsReinstatedOnlyOnAnAdminToStaffFlip(
        string? previous, string next, bool shouldReinstate)
    {
        var flip = InstalledRoleWriter.IsAdminRoleValue(previous)
                   && string.Equals(next, InstalledRoleWriter.Staff, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(shouldReinstate, flip);
    }
}
