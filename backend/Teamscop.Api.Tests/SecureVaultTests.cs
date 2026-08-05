using Teamscop.Engine.Tracking;

namespace Teamscop.Api.Tests;

public class SecureVaultTests
{
    [Fact]
    public void Append_And_Verify_Chain()
    {
        var root = Path.Combine(Path.GetTempPath(), "vault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var key = SecureVault.DeriveMasterKey("device-abc", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
            var vault = new SecureVault(root, key);
            vault.Append(new VaultRecord { Kind = "timetrack", OccurredAt = DateTimeOffset.UtcNow, PlainPayload = "one"u8.ToArray() });
            vault.Append(new VaultRecord { Kind = "timetrack", OccurredAt = DateTimeOffset.UtcNow, PlainPayload = "two"u8.ToArray() });
            var report = vault.Verify(fullScan: true);
            Assert.True(report.Ok, report.Error);
            Assert.Equal(2, report.RecordCount);
            Assert.Equal(3, report.ExpectedNextSequence);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Tamper_Is_Detected()
    {
        var root = Path.Combine(Path.GetTempPath(), "vault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var key = SecureVault.DeriveMasterKey("device-abc", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
            var vault = new SecureVault(root, key);
            var append = vault.Append(new VaultRecord { Kind = "x", OccurredAt = DateTimeOffset.UtcNow, PlainPayload = "data"u8.ToArray() });
            var bytes = File.ReadAllBytes(append.FilePath);
            bytes[^40] ^= 0xFF;
            File.WriteAllBytes(append.FilePath, bytes);
            var report = vault.Verify(fullScan: true);
            Assert.False(report.Ok);
            Assert.True(report.TamperedRecord || report.ChainBreak);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Deletion_Gap_Is_Detected()
    {
        var root = Path.Combine(Path.GetTempPath(), "vault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var key = SecureVault.DeriveMasterKey("device-abc", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
            var vault = new SecureVault(root, key);
            var a = vault.Append(new VaultRecord { Kind = "a", OccurredAt = DateTimeOffset.UtcNow, PlainPayload = "1"u8.ToArray() });
            vault.Append(new VaultRecord { Kind = "b", OccurredAt = DateTimeOffset.UtcNow, PlainPayload = "2"u8.ToArray() });
            File.Delete(a.FilePath);
            var report = vault.Verify(fullScan: true);
            Assert.False(report.Ok);
            Assert.True(report.ChainBreak);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
