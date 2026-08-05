using Teamscop.Engine.Auth;

namespace Teamscop.Api.Tests;

public class CompanyTokenCodecTests
{
    private static readonly byte[] Key = Convert.FromBase64String("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");

    [Fact]
    public void Encrypt_Decrypt_RoundTripsPayload()
    {
        var codec = new CompanyTokenCodec(Key);
        var payload = new CompanyTokenPayload
        {
            Version = 1,
            CompanyId = Guid.NewGuid(),
            CompanyName = "Acme Corp",
            AdminUserId = Guid.NewGuid(),
            IssuedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Jti = Guid.NewGuid()
        };

        var token = codec.Encrypt(payload);
        Assert.StartsWith("TS1.", token);
        Assert.True(codec.TryDecrypt(token, out var restored));
        Assert.NotNull(restored);
        Assert.Equal(payload.CompanyId, restored!.CompanyId);
        Assert.Equal(payload.CompanyName, restored.CompanyName);
        Assert.Equal(payload.AdminUserId, restored.AdminUserId);
        Assert.Equal(payload.Jti, restored.Jti);
    }

    [Fact]
    public void TryDecrypt_RejectsTamperedToken()
    {
        var codec = new CompanyTokenCodec(Key);
        var token = codec.Encrypt(new CompanyTokenPayload
        {
            CompanyId = Guid.NewGuid(),
            CompanyName = "X",
            AdminUserId = Guid.NewGuid(),
            IssuedAtUnix = 1,
            Jti = Guid.NewGuid()
        });

        var chars = token.ToCharArray();
        chars[^1] = chars[^1] == 'A' ? 'B' : 'A';
        Assert.False(codec.TryDecrypt(new string(chars), out _));
    }

    [Fact]
    public void DeviceKeyHash_IsStable()
    {
        var a = DeviceKeyProvider.HashNormalized("UUID-1", "BOARD", "BIOS");
        var b = DeviceKeyProvider.HashNormalized("uuid-1", " board ", "bios");
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
    }
}
