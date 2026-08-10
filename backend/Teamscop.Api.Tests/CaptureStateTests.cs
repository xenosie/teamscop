using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Tracking;

namespace Teamscop.Api.Tests;

/// <summary>
/// §14.1 — the machine's own verdict on its capture pipeline.
///
/// Every test here except the last two asserts that something is NOT a fault. That balance is the
/// point: the server takes this verdict at face value and turns "broken" into a black dot telling
/// the owner an employee interfered with their PC. Each of these cases produced exactly that
/// accusation under a naive "no helper means sabotage" rule.
/// </summary>
public class CaptureStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    private static string Evaluate(
        bool enabled = true,
        bool anyUser = true,
        bool consoleUser = true,
        bool remoteOnly = false,
        TimeSpan? consoleFor = null,
        bool helperAlive = true,
        bool trackingHealthy = true,
        string? reason = null)
        => CaptureState.Evaluate(
            anyCaptureEnabled: enabled,
            sessions: new SessionSnapshot(anyUser, consoleUser, remoteOnly),
            consoleUserSince: consoleUser ? Now - (consoleFor ?? TimeSpan.FromHours(1)) : null,
            helperAlive: helperAlive,
            trackingHealthy: trackingHealthy,
            unavailableReason: reason,
            now: Now).State;

    [Fact]
    public void EverythingWorking_IsOk()
        => Assert.Equal(CaptureState.Ok, Evaluate());

    /// <summary>The admin turned capture off. Nothing is expected, so nothing is wrong.</summary>
    [Fact]
    public void NoCaptureTypeEnabled_IsDisabled_NotBroken()
        => Assert.Equal(CaptureState.Disabled, Evaluate(enabled: false, helperAlive: false, trackingHealthy: false));

    /// <summary>
    /// The overnight and weekend case, and the one most likely to be got wrong. Nobody is signed in,
    /// so there is no screen to capture — a closed office, not a sabotaged machine.
    /// </summary>
    [Fact]
    public void NobodyLoggedOn_IsIdle_NotBroken()
        => Assert.Equal(
            CaptureState.IdleNoUser,
            Evaluate(anyUser: false, consoleUser: false, helperAlive: false, trackingHealthy: false));

    /// <summary>
    /// Signed in over RDP only. The helper launches into the console session, so capture genuinely
    /// cannot happen — a product limitation, and calling a limitation sabotage would be dishonest.
    /// </summary>
    [Fact]
    public void RemoteSessionOnly_IsUnsupported_NotBroken()
        => Assert.Equal(
            CaptureState.UnsupportedSession,
            Evaluate(consoleUser: false, remoteOnly: true, helperAlive: false, trackingHealthy: false));

    /// <summary>Just signed in. The supervisor needs a moment to launch the helper.</summary>
    [Fact]
    public void JustSignedIn_IsStarting_NotBroken()
        => Assert.Equal(
            CaptureState.Starting,
            Evaluate(consoleFor: TimeSpan.FromMinutes(1), helperAlive: false, trackingHealthy: false));

    [Fact]
    public void SignedInLongEnoughAndNoHelper_IsBroken_CarryingTheEnginesReason()
    {
        var verdict = CaptureState.Evaluate(
            anyCaptureEnabled: true,
            sessions: new SessionSnapshot(true, true, false),
            consoleUserSince: Now - TimeSpan.FromHours(1),
            helperAlive: false,
            trackingHealthy: false,
            unavailableReason: "no_session_helper_stale",
            now: Now);

        Assert.True(verdict.IsBroken);
        Assert.Equal("no_session_helper_stale", verdict.Reason);
    }

    /// <summary>The helper is connected but nothing is coming out of it — a wedged pipeline.</summary>
    [Fact]
    public void HelperAliveButNothingCaptured_IsBroken()
        => Assert.Equal(CaptureState.Broken, Evaluate(trackingHealthy: false));
}

/// <summary>
/// §14.3 — detecting a gutted installation. Deleting binaries out of the install directory leaves
/// the running processes going on the code they already hold in memory, so a machine can report
/// itself perfectly healthy while the product has been hollowed out.
/// </summary>
public class ComponentInventoryTests
{
    [Fact]
    public void IntactDirectory_ReportsNothingMissing()
    {
        var dir = NewDir();
        foreach (var name in ComponentInventory.ServiceSideComponents)
        {
            File.WriteAllText(Path.Combine(dir, name), "x");
        }

        Assert.Empty(ComponentInventory.MissingFrom(dir, ComponentInventory.ServiceSideComponents));
    }

    [Fact]
    public void DeletedBinary_IsReportedByName()
    {
        var dir = NewDir();
        var expected = ComponentInventory.ServiceSideComponents;
        foreach (var name in expected.Skip(1))
        {
            File.WriteAllText(Path.Combine(dir, name), "x");
        }

        var missing = ComponentInventory.MissingFrom(dir, expected);

        Assert.Single(missing);
        Assert.Equal(expected[0], missing[0]);
    }

    [Fact]
    public void MissingDirectory_ReportsEverythingMissing()
        => Assert.Equal(
            ComponentInventory.ServiceSideComponents.Count,
            ComponentInventory.MissingFrom(
                Path.Combine(Path.GetTempPath(), "teamscop-not-here-" + Guid.NewGuid().ToString("N")),
                ComponentInventory.ServiceSideComponents).Count);

    /// <summary>
    /// Each half attests the other's files, so neither can vouch for itself. The service does not
    /// check its own binary because a deleted service cannot report anything at all.
    /// </summary>
    [Fact]
    public void TheTwoSidesCheckDifferentFiles()
    {
        Assert.DoesNotContain("Teamscop.StaffService.exe", ComponentInventory.ServiceSideComponents);
        Assert.Contains("Teamscop.StaffService.exe", ComponentInventory.AppSideComponents);
        Assert.Contains("Teamscop.App.exe", ComponentInventory.ServiceSideComponents);
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "teamscop-inv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
