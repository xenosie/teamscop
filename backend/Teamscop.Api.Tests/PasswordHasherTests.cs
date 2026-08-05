using Teamscop.Api.Services;

namespace Teamscop.Api.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_And_Verify_Succeeds()
    {
        var hasher = new Argon2PasswordHasher();
        var encoded = hasher.Hash("correct-horse-battery");
        Assert.StartsWith("argon2id$", encoded);
        Assert.True(hasher.Verify("correct-horse-battery", encoded));
        Assert.False(hasher.Verify("wrong-password", encoded));
    }
}
