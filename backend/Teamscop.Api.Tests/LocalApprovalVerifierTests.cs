using System.Globalization;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.Api.Tests;

/// <summary>
/// §6 / §9.6 / §11.2 — the offline half of approval codes, in the derived model. There is no stored
/// secret and no enrolment: the verifier derives this machine's code from its device key alone (the
/// single <see cref="TotpGenerator.DeriveMachineSecret"/>) on company-local time, so a code the
/// server generated verifies here with no network — a USB stick inserted on a train, an uninstall
/// in a hotel room. Every check that matters is here: cross-side determinism, purpose separation,
/// replay, lockout, and what happens with no device key.
///
/// The clock is injected as the COMPANY-LOCAL instant (§6.3), so the 15-minute lockout is exercised
/// in microseconds and the result never depends on which 30-second step the suite happened to run in.
/// </summary>
public class LocalApprovalVerifierTests
{
    /// <summary>A fixed device key so every code in this file is a constant.</summary>
    private const string DeviceKey = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4";

    /// <summary>The company-local instant the injected clock reports (§6.3 treats it as zero-offset).</summary>
    private static readonly DateTimeOffset At = new(2026, 6, 1, 12, 0, 15, TimeSpan.Zero);

    private static string UsbSecret => TotpGenerator.DeriveMachineSecret(DeviceKey, TotpGenerator.PurposeUsb);

    private static string UninstallSecret => TotpGenerator.DeriveMachineSecret(DeviceKey, TotpGenerator.PurposeUninstall);

    [Fact]
    public void ACodeTheServerGenerated_VerifiesOfflineFromTheSameDeviceKey_ForEachPurpose()
    {
        using var root = new AgentTestRoot("approval");
        var (verifier, _) = Provision(root, At);

        Assert.True(verifier.HasSecret(ApprovalPurpose.Usb));
        Assert.True(verifier.HasSecret(ApprovalPurpose.Uninstall));

        // §6.4 — the server computed this from users.DeviceKey on the company-local step; the agent
        // re-derives the identical secret from its own device key and verifies with no server call.
        var usb = verifier.Verify(ApprovalPurpose.Usb, TotpGenerator.ComputeCode(UsbSecret, At));
        Assert.True(usb.Ok);
        Assert.Equal(ApprovalRefusal.None, usb.Refusal);
        Assert.Equal(TotpGenerator.GetTimeStep(At), usb.MatchedStep);

        var uninstall = verifier.Verify(ApprovalPurpose.Uninstall, TotpGenerator.ComputeCode(UninstallSecret, At));
        Assert.True(uninstall.Ok);
    }

    [Fact]
    public void ACodeStillWorksThirtySecondsEitherSide_OfTheAdminReadingItOut()
    {
        using var root = new AgentTestRoot("approval");
        var (verifier, clock) = Provision(root, At);

        // §10.2 — the admin reads the code down the phone, so a one-step window each way is the
        // difference between a working feature and an employee retyping until they are locked out.
        var early = TotpGenerator.ComputeCode(UsbSecret, At.AddSeconds(-30));
        Assert.True(verifier.Verify(ApprovalPurpose.Usb, early).Ok);

        clock.Advance(TimeSpan.FromSeconds(60));
        var late = TotpGenerator.ComputeCode(UsbSecret, At.AddSeconds(90));
        Assert.True(verifier.Verify(ApprovalPurpose.Usb, late).Ok);

        // Two steps out is outside the window and must not be honoured.
        clock.Advance(TimeSpan.FromSeconds(300));
        var stale = TotpGenerator.ComputeCode(UsbSecret, clock.Now.AddSeconds(-90));
        Assert.False(verifier.Verify(ApprovalPurpose.Usb, stale).Ok);
    }

    [Fact]
    public void AUsbCodeCannotOpenAnUninstall_AndTheReverseAlsoHolds()
    {
        using var root = new AgentTestRoot("approval");
        var (verifier, _) = Provision(root, At);

        var usbCode = TotpGenerator.ComputeCode(UsbSecret, At);
        var uninstallCode = TotpGenerator.ComputeCode(UninstallSecret, At);

        // §10.1/§11.1 — the two are independent code streams (HKDF info = purpose). Handing an
        // employee a stick-approval code must never be handing them permission to remove the agent.
        Assert.NotEqual(usbCode, uninstallCode);

        var openUninstallWithUsb = verifier.Verify(ApprovalPurpose.Uninstall, usbCode);
        Assert.False(openUninstallWithUsb.Ok);
        Assert.Equal(ApprovalRefusal.Invalid, openUninstallWithUsb.Refusal);

        var openUsbWithUninstall = verifier.Verify(ApprovalPurpose.Usb, uninstallCode);
        Assert.False(openUsbWithUninstall.Ok);
        Assert.Equal(ApprovalRefusal.Invalid, openUsbWithUninstall.Refusal);
    }

    [Fact]
    public void AUsedCodeIsRefusedLocally_WithoutAskingTheServer()
    {
        using var root = new AgentTestRoot("approval");
        var (verifier, _) = Provision(root, At);

        var code = TotpGenerator.ComputeCode(UsbSecret, At);
        Assert.True(verifier.Verify(ApprovalPurpose.Usb, code).Ok);

        // The admin reads one code out of band; it opens one thing, once. There is no network here
        // to consult, so the watermark on disk is the entire defence.
        var replay = verifier.Verify(ApprovalPurpose.Usb, code);
        Assert.False(replay.Ok);
        Assert.Equal(ApprovalRefusal.AlreadyUsed, replay.Refusal);

        // An older step is equally spent — otherwise the previous minute's code is a second key.
        var older = TotpGenerator.ComputeCode(UsbSecret, At.AddSeconds(-30));
        Assert.Equal(ApprovalRefusal.AlreadyUsed, verifier.Verify(ApprovalPurpose.Usb, older).Refusal);
    }

    [Fact]
    public void ReplayRefusalSurvivesARestart()
    {
        using var root = new AgentTestRoot("approval");
        var (verifier, clock) = Provision(root, At);

        var code = TotpGenerator.ComputeCode(UsbSecret, At);
        Assert.True(verifier.Verify(ApprovalPurpose.Usb, code).Ok);

        // The uninstall guard is a separate short-lived process, and the service restarts. If the
        // watermark lived in memory a replay would only have to wait for either to happen.
        var afterRestart = new LocalApprovalVerifier(() => DeviceKey, root.Path, clock.Func);
        Assert.Equal(ApprovalRefusal.AlreadyUsed, afterRestart.Verify(ApprovalPurpose.Usb, code).Refusal);
    }

    [Fact]
    public void TheTwoPurposesKeepSeparateReplayWatermarks()
    {
        using var root = new AgentTestRoot("approval");
        var (verifier, _) = Provision(root, At);

        Assert.True(verifier.Verify(ApprovalPurpose.Usb, TotpGenerator.ComputeCode(UsbSecret, At)).Ok);

        // Spending the USB code for this step must not also spend the uninstall code for it.
        Assert.True(verifier.Verify(ApprovalPurpose.Uninstall, TotpGenerator.ComputeCode(UninstallSecret, At)).Ok);
    }

    [Fact]
    public void WithNoDeviceKey_EverythingFailsClosed()
    {
        using var root = new AgentTestRoot("approval");
        var clock = new TestClock(At);
        // A machine with no device key cannot derive anything — the degenerate not-enrolled state.
        var verifier = new LocalApprovalVerifier(() => null, root.Path, clock.Func);

        Assert.False(verifier.HasSecret(ApprovalPurpose.Usb));
        Assert.False(verifier.HasSecret(ApprovalPurpose.Uninstall));

        var check = verifier.Verify(ApprovalPurpose.Usb, "123456");
        Assert.False(check.Ok);
        Assert.Equal(ApprovalRefusal.NoSecret, check.Refusal);

        Assert.Equal(ApprovalRefusal.NoSecret, verifier.Verify(ApprovalPurpose.Uninstall, "123456").Refusal);
    }

    [Fact]
    public void AMalformedCodeIsRefusedWithoutTouchingTheSecret()
    {
        using var root = new AgentTestRoot("approval");
        var (verifier, _) = Provision(root, At);

        foreach (var bad in new[] { null, "", "   ", "12345", "1234567", "12345a", "abcdef" })
        {
            var check = verifier.Verify(ApprovalPurpose.Usb, bad);
            Assert.False(check.Ok);
            Assert.Equal(ApprovalRefusal.Malformed, check.Refusal);
        }

        // A code pasted out of a chat message arrives with whitespace, and must still work.
        var padded = "  " + TotpGenerator.ComputeCode(UsbSecret, At) + "\r\n";
        Assert.True(verifier.Verify(ApprovalPurpose.Usb, padded).Ok);
    }

    [Fact]
    public void EightWrongCodesLockTheStickerOut_AndTheLockoutExpiresOnItsOwn()
    {
        using var root = new AgentTestRoot("approval");
        var (verifier, clock) = Provision(root, At);
        var wrong = WrongCode(UsbSecret, At);

        for (var attempt = 1; attempt <= 8; attempt++)
        {
            Assert.Equal(ApprovalRefusal.Invalid, verifier.Verify(ApprovalPurpose.Usb, wrong).Refusal);
        }

        // Even the right code is refused while the lockout holds — brute force is the threat, and
        // §10.3 accepts that the secret is derivable on the machine, so guessing is all that is left.
        var correct = TotpGenerator.ComputeCode(UsbSecret, clock.Now);
        Assert.Equal(ApprovalRefusal.LockedOut, verifier.Verify(ApprovalPurpose.Usb, correct).Refusal);

        clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));

        // §11.2/§9.6 — an offline machine has nobody to appeal to, so the lockout has to clear
        // itself or one fat-fingered employee is permanently unable to approve anything.
        var afterCooldown = TotpGenerator.ComputeCode(UsbSecret, clock.Now);
        Assert.True(verifier.Verify(ApprovalPurpose.Usb, afterCooldown).Ok);
    }

    [Fact]
    public void TheFailureCounterDecays_SoAPrimedCounterCannotLockOutTheNextGenuineTypo()
    {
        using var root = new AgentTestRoot("approval");
        var (verifier, clock) = Provision(root, At);

        // B15 — without decay, anyone who can reach the sticker leaves the counter at seven and the
        // employee's next single mistake costs them fifteen minutes.
        for (var attempt = 1; attempt <= 7; attempt++)
        {
            verifier.Verify(ApprovalPurpose.Usb, WrongCode(UsbSecret, clock.Now));
        }

        clock.Advance(TimeSpan.FromMinutes(16));

        Assert.Equal(
            ApprovalRefusal.Invalid,
            verifier.Verify(ApprovalPurpose.Usb, WrongCode(UsbSecret, clock.Now)).Refusal);
        Assert.True(verifier.Verify(ApprovalPurpose.Usb, TotpGenerator.ComputeCode(UsbSecret, clock.Now)).Ok);
    }

    [Fact]
    public void ASuccessClearsTheFailureCount()
    {
        using var root = new AgentTestRoot("approval");
        var (verifier, clock) = Provision(root, At);

        for (var attempt = 1; attempt <= 7; attempt++)
        {
            verifier.Verify(ApprovalPurpose.Usb, WrongCode(UsbSecret, clock.Now));
        }

        Assert.True(verifier.Verify(ApprovalPurpose.Usb, TotpGenerator.ComputeCode(UsbSecret, clock.Now)).Ok);

        clock.Advance(TimeSpan.FromSeconds(60));
        for (var attempt = 1; attempt <= 7; attempt++)
        {
            Assert.Equal(
                ApprovalRefusal.Invalid,
                verifier.Verify(ApprovalPurpose.Usb, WrongCode(UsbSecret, clock.Now)).Refusal);
        }
    }

    private static (LocalApprovalVerifier Verifier, TestClock Clock) Provision(AgentTestRoot root, DateTimeOffset at)
    {
        var clock = new TestClock(at);
        return (new LocalApprovalVerifier(() => DeviceKey, root.Path, clock.Func), clock);
    }

    /// <summary>
    /// A six-digit code this secret does not accept at this instant, found rather than assumed. A
    /// hard-coded "000000" is right 999999 times in a million, and the millionth run is a mystery.
    /// </summary>
    private static string WrongCode(string secret, DateTimeOffset at)
    {
        for (var candidate = 0; candidate < 64; candidate++)
        {
            var code = candidate.ToString("D6", CultureInfo.InvariantCulture);
            if (!TotpGenerator.VerifyCode(secret, code, 1, at))
            {
                return code;
            }
        }

        throw new InvalidOperationException("Could not find a code this secret rejects.");
    }
}
