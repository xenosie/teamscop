using System.Text;
using Teamscop.Engine.Tracking;

namespace Teamscop.Api.Tests;

/// <summary>
/// The reduced vault (§1.2): AES-256-GCM encryption + Brotli compression + a per-record file, with
/// crash-safe commit and never-drop recovery. The tamper-evident hash chain, tip MAC and integrity
/// verification were removed (§1.1); these tests cover only what remains — that captured data round
/// trips through encryption, and that a crash between the vault commit and the outbox enqueue never
/// loses a record.
/// </summary>
public class SecureVaultTests
{
    private const string Key64 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    [Fact]
    public void Append_EncryptsOnDisk_AndReadsBackThePlaintext()
    {
        var root = NewRoot();
        try
        {
            var key = SecureVault.DeriveMasterKey("device-abc", Key64);
            var vault = new SecureVault(root, key);
            var append = vault.Append(Record("the quick brown fox"));

            // On disk the payload is not readable — the plaintext must not appear in the record file.
            var onDisk = File.ReadAllBytes(append.FilePath);
            Assert.DoesNotContain("the quick brown fox", Encoding.UTF8.GetString(onDisk), StringComparison.Ordinal);

            // But the vault decrypts it back for re-delivery.
            var pending = new SecureVault(root, key).ReadUnenqueued();
            var only = Assert.Single(pending);
            Assert.Equal("the quick brown fox", Encoding.UTF8.GetString(only.PlainPayload));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CrashBeforeTip_DiscardsTheOrphanRecord_OnReopen()
    {
        var root = NewRoot();
        try
        {
            var key = SecureVault.DeriveMasterKey("device-abc", Key64);
            var vault = new SecureVault(root, key);
            vault.Append(Record("one"));
            var second = vault.Append(Record("two")); // tip.NextSequence == 3 after this

            // Simulate a crash after the record for sequence 3 was written but before the tip
            // advanced: a file named for sequence 3 exists, yet the committed tip still says the
            // next sequence is 3, so it was never committed.
            var orphan = Path.Combine(root, "vault", "records", "00000000000000000003.tv1");
            File.Copy(second.FilePath, orphan);

            // Reopening runs crash repair, which drops the uncommitted remainder.
            var reopened = new SecureVault(root, key);

            Assert.False(File.Exists(orphan));
            Assert.Equal(2, reopened.ReadUnenqueued().Count);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CommittedButUnenqueuedRecords_AreReadable_ThenClearedByMarkEnqueued()
    {
        var root = NewRoot();
        try
        {
            var key = SecureVault.DeriveMasterKey("device-abc", Key64);
            var vault = new SecureVault(root, key);
            vault.Append(Record("one"));
            vault.Append(Record("two"));
            // No MarkEnqueued: models a crash between the vault commit and the outbox enqueue.

            var reopened = new SecureVault(root, key);
            var pending = reopened.ReadUnenqueued();

            Assert.Equal(2, pending.Count);
            Assert.Equal(1, pending[0].Sequence);
            Assert.Equal("one", Encoding.UTF8.GetString(pending[0].PlainPayload));
            Assert.Equal("two", Encoding.UTF8.GetString(pending[1].PlainPayload));

            // Confirming delivery of the highest sequence clears the backlog and never returns it again.
            reopened.MarkEnqueued(pending[^1].Sequence);
            Assert.Empty(reopened.ReadUnenqueued());
            Assert.Empty(new SecureVault(root, key).ReadUnenqueued());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MarkEnqueuedPerRecord_LeavesNoBacklog()
    {
        var root = NewRoot();
        try
        {
            var key = SecureVault.DeriveMasterKey("device-abc", Key64);
            var vault = new SecureVault(root, key);
            for (var i = 0; i < 3; i++)
            {
                var append = vault.Append(Record($"r{i}"));
                vault.MarkEnqueued(append.Sequence);
            }

            Assert.Empty(vault.ReadUnenqueued());
            Assert.Empty(new SecureVault(root, key).ReadUnenqueued());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ALostTip_RebuildsTheSequenceCounter_WithoutOverwritingRecords()
    {
        var root = NewRoot();
        try
        {
            var key = SecureVault.DeriveMasterKey("device-abc", Key64);
            var vault = new SecureVault(root, key);
            vault.Append(Record("one"));
            var second = vault.Append(Record("two"));
            vault.MarkEnqueued(second.Sequence);

            // The tip file is lost, but the two record files survive.
            File.Delete(Path.Combine(root, "vault", "tip.bin"));

            // The counter rebuilds from the highest record filename, so the next append does not
            // overwrite an existing record — it lands at sequence 3.
            var reopened = new SecureVault(root, key);
            var third = reopened.Append(Record("three"));
            Assert.Equal(3, third.Sequence);
            Assert.True(File.Exists(Path.Combine(root, "vault", "records", "00000000000000000001.tv1")));
            Assert.True(File.Exists(Path.Combine(root, "vault", "records", "00000000000000000002.tv1")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "vault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static VaultRecord Record(string payload)
        => new() { Kind = "timetrack", OccurredAt = DateTimeOffset.UtcNow, PlainPayload = Encoding.UTF8.GetBytes(payload) };
}
