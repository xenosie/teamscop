using Teamscop.Api.Services;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Tracking;

namespace Teamscop.Api.Tests;

/// <summary>
/// §6.3 / §6.6 — the derived code runs on COMPANY-LOCAL time, and the server (code generation via
/// <see cref="CompanyBusinessTime"/>) and the agent (offline verify via <see cref="BusinessClock"/>)
/// derive that local time through two different implementations. The owner's rule is one frame of
/// reference; the silent-failure mode is those two implementations drifting near a DST change. These
/// pin that they land on the identical company-local instant, so a code the server reads out always
/// verifies offline — no per-service timezone conversion, no drift.
/// </summary>
public class CodesCompanyLocalParityTests
{
    private const string DeviceKey = "feedface00112233feedface00112233";

    [Theory]
    [InlineData("UTC+03:00")]     // fixed offset
    [InlineData("Europe/Berlin")] // DST zone, whole-hour offset
    [InlineData("Asia/Kolkata")]  // +05:30, a half-hour offset — still a multiple of the 30 s step
    public void ServerAndAgent_ComputeTheSameCompanyLocalInstant_SoTheServersCodeVerifiesOffline(string tz)
    {
        Assert.True(CompanyBusinessTime.TryResolve(tz, out _), $"test zone {tz} did not resolve");
        var utcNow = new DateTimeOffset(2026, 8, 8, 10, 17, 20, TimeSpan.Zero);

        // Server: company-local instant the code generator uses.
        var serverNow = new DateTimeOffset(
            CompanyBusinessTime.ToBusinessLocal(utcNow, CompanyBusinessTime.Resolve(tz)), TimeSpan.Zero);

        // Agent: company-local instant the live BusinessClock the verifier reads produces.
        var clock = new BusinessClock();
        clock.Apply(new BusinessClockConfig { TimeZoneId = tz });
        var agentNow = new DateTimeOffset(clock.At(utcNow).BusinessLocal, TimeSpan.Zero);

        // One rule, one source: identical instant, therefore identical step (§6.6).
        Assert.Equal(serverNow, agentNow);
        Assert.Equal(TotpGenerator.GetTimeStep(serverNow), TotpGenerator.GetTimeStep(agentNow));

        // The code the server generated verifies offline on the agent.
        var serverCode = TotpGenerator.ComputeCode(
            TotpGenerator.DeriveMachineSecret(DeviceKey, TotpGenerator.PurposeUsb), serverNow);

        using var root = new AgentTestRoot("parity");
        var verifier = new LocalApprovalVerifier(() => DeviceKey, root.Path, () => agentNow);
        Assert.True(verifier.Verify(ApprovalPurpose.Usb, serverCode).Ok);
    }

    [Fact]
    public void AcrossADstTransition_TheTwoImplementationsStillAgree()
    {
        // Berlin springs forward at 01:00 UTC on 2026-03-29. The code STREAM is discontinuous there
        // (±1 h = ±120 steps) — the accepted §6.6 weakness — but the two IMPLEMENTATIONS must not
        // drift, because both read the same OS tz database. Sample either side of the boundary.
        var zone = CompanyBusinessTime.Resolve("Europe/Berlin");
        var clock = new BusinessClock();
        clock.Apply(new BusinessClockConfig { TimeZoneId = "Europe/Berlin" });

        foreach (var utc in new[]
                 {
                     new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero), // before (+01:00)
                     new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), // after  (+02:00)
                 })
        {
            Assert.Equal(CompanyBusinessTime.ToBusinessLocal(utc, zone), clock.At(utc).BusinessLocal);
        }
    }
}
