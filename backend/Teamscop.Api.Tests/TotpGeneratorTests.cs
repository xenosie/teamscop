using Teamscop.Engine.Lifecycle;

namespace Teamscop.Api.Tests;

public class TotpGeneratorTests
{
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
}
