using Teamscop.Engine.Lifecycle;

namespace Teamscop.Api.Tests;

public class TotpGeneratorTests
{
    [Fact]
    public void VerifyCode_ReturnsMatchedStep()
    {
        var secret = TotpGenerator.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        var step = TotpGenerator.GetTimeStep(now);
        var code = TotpGenerator.ComputeCode(secret, now);
        Assert.True(TotpGenerator.VerifyCode(secret, code, window: 1, now, out var matched));
        Assert.Equal(step, matched);
    }

    [Fact]
    public void Compute_And_Verify_WithinWindow()
    {
        var secret = TotpGenerator.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        var code = TotpGenerator.ComputeCode(secret, now);
        Assert.Equal(6, code.Length);
        Assert.True(TotpGenerator.VerifyCode(secret, code, window: 1, utcNow: now));
        Assert.False(TotpGenerator.VerifyCode(secret, "000000", window: 0, utcNow: now.AddSeconds(90)));
    }

    [Fact]
    public void OtpAuthUri_ContainsSecret()
    {
        var secret = TotpGenerator.GenerateSecret();
        var uri = TotpGenerator.BuildOtpAuthUri(secret, "Teamscop", "Acme");
        Assert.Contains(secret, uri);
        Assert.StartsWith("otpauth://totp/", uri);
    }

    // ---- §6 derived-machine-secret contract -------------------------------------------------

    [Fact]
    public void DeriveMachineSecret_IsDeterministic_ForTheSameDeviceKeyAndPurpose()
    {
        const string device = "ABCDEF0123456789ABCDEF0123456789";
        Assert.Equal(
            TotpGenerator.DeriveMachineSecret(device, TotpGenerator.PurposeUsb),
            TotpGenerator.DeriveMachineSecret(device, TotpGenerator.PurposeUsb));
    }

    [Fact]
    public void DeriveMachineSecret_NormalizesTheDeviceKey_SoServerAndAgentAlwaysAgree()
    {
        // The server stores the device key lowercased; an agent might hand it back trimmed/upper.
        // The normalization lives INSIDE the function so both sides derive the identical secret —
        // a byte difference here and codes silently stop matching (§6.5).
        Assert.Equal(
            TotpGenerator.DeriveMachineSecret("abc123def456", TotpGenerator.PurposeUsb),
            TotpGenerator.DeriveMachineSecret("  ABC123DEF456  ", TotpGenerator.PurposeUsb));
    }

    [Fact]
    public void DeriveMachineSecret_SeparatesUsbAndUninstall_IntoIndependentStreams()
    {
        const string device = "0011223344556677";
        var usb = TotpGenerator.DeriveMachineSecret(device, TotpGenerator.PurposeUsb);
        var uninstall = TotpGenerator.DeriveMachineSecret(device, TotpGenerator.PurposeUninstall);
        Assert.NotEqual(usb, uninstall);

        var now = DateTimeOffset.UtcNow;
        // A USB code can never verify as an uninstall code.
        Assert.False(TotpGenerator.VerifyCode(uninstall, TotpGenerator.ComputeCode(usb, now), window: 1, now));
    }

    [Fact]
    public void DeriveMachineSecret_DiffersByDeviceKey()
    {
        Assert.NotEqual(
            TotpGenerator.DeriveMachineSecret("device-one", TotpGenerator.PurposeUsb),
            TotpGenerator.DeriveMachineSecret("device-two", TotpGenerator.PurposeUsb));
    }

    [Fact]
    public void DeriveMachineSecret_CompanyLocalStep_ShiftsTheCodeByTheOffset_ButNotThePeriodBoundary()
    {
        // §6.3 — the step counter runs on company-local time, implemented as "treat the company-local
        // wall clock as a zero-offset instant". A whole-hour offset shifts the step by 120 (= 3600/30)
        // but keeps the 30-second boundary aligned, so the countdown is unchanged.
        const string device = "aabbccddeeff00112233";
        var secret = TotpGenerator.DeriveMachineSecret(device, TotpGenerator.PurposeUsb);

        var utcNow = new DateTimeOffset(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);
        var utcCode = TotpGenerator.ComputeCode(secret, utcNow);

        // +02:00 company-local instant for the same real moment.
        var companyLocal = new DateTimeOffset(utcNow.UtcDateTime.AddHours(2), TimeSpan.Zero);
        var companyCode = TotpGenerator.ComputeCode(secret, companyLocal);

        Assert.NotEqual(utcCode, companyCode);
        Assert.Equal(TotpGenerator.GetTimeStep(utcNow) + (2 * 3600 / 30), TotpGenerator.GetTimeStep(companyLocal));
    }
}
