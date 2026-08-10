using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Teamscop.Engine.Auth;

namespace Teamscop.Engine.Tracking;

public sealed class VaultRecord
{
    public long Sequence { get; set; }
    public Guid RecordId { get; set; } = Guid.NewGuid();
    public required string Kind { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public required byte[] PlainPayload { get; set; }
}

public sealed class VaultAppendResult
{
    public required long Sequence { get; init; }
    public required string FilePath { get; init; }
}

/// <summary>
/// A committed vault record decrypted for re-delivery (§13.1). Carries the fields the outbox
/// envelope needs, and <see cref="RecordId"/> so a crash-recovery re-enqueue reuses the original
/// client-event id and the server deduplicates rather than double-counting.
/// </summary>
public sealed class VaultPendingRecord
{
    public required long Sequence { get; init; }
    public required Guid RecordId { get; init; }
    public required string Kind { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required byte[] PlainPayload { get; init; }
}

/// <summary>
/// Local encrypted store (§1.2): Brotli compress → AES-256-GCM encrypt → append to a per-record
/// file. Designed for low CPU: small records, an O(1) tip update on the hot path instead of a
/// folder scan. Encryption keeps captured data unreadable on disk by the employee.
///
/// <para>
/// The sequence number is an internal ordering / commit counter only — it names the record file and
/// backs the never-drop recovery watermark. It carries no cryptographic meaning: integrity of the
/// commit point now rests on the atomic tip rename, and the tamper-evident hash chain that used to
/// ride alongside every record has been removed (§1.1).
/// </para>
/// </summary>
public sealed class SecureVault
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>header = seq(8) + recordId(16) + occurredUnix(8) + kindLen(2).</summary>
    private const int HeaderSize = 8 + 16 + 8 + 2;

    private readonly string _recordsDir;
    private readonly string _tipPath;
    private readonly byte[] _encKey;
    private readonly object _gate = new();

    public SecureVault(string rootDirectory, byte[] masterKey32)
    {
        if (masterKey32.Length != 32)
        {
            throw new ArgumentException("Vault master key must be 32 bytes.", nameof(masterKey32));
        }

        _recordsDir = Path.Combine(rootDirectory, "vault", "records");
        _tipPath = Path.Combine(rootDirectory, "vault", "tip.bin");
        Directory.CreateDirectory(_recordsDir);

        _encKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey32, 32, info: Encoding.UTF8.GetBytes("teamscop-vault-enc-v1"));

        DiscardUncommittedRecords();
        MigrateEnqueuedWatermark();
    }

    public static byte[] DeriveMasterKey(string deviceKey, string companyTokenKeyBase64)
    {
        var material = Encoding.UTF8.GetBytes(deviceKey.Trim().ToLowerInvariant() + "|" + companyTokenKeyBase64.Trim());
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, material, 32, info: Encoding.UTF8.GetBytes("teamscop-vault-master-v1"));
    }

    public VaultAppendResult Append(VaultRecord record)
    {
        lock (_gate)
        {
            var tip = ReadTipUnsafe();
            var seq = tip.NextSequence;
            var compressed = BrotliCompress(record.PlainPayload);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var cipher = new byte[compressed.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(_encKey, TagSize))
            {
                aes.Encrypt(nonce, compressed, cipher, tag);
            }

            var kindBytes = Encoding.UTF8.GetBytes(record.Kind);
            if (kindBytes.Length > 64)
            {
                throw new InvalidOperationException("Kind too long.");
            }

            var header = new byte[HeaderSize];
            BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(0, 8), seq);
            record.RecordId.ToByteArray().CopyTo(header, 8);
            BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(24, 8), record.OccurredAt.ToUnixTimeSeconds());
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(32, 2), (ushort)kindBytes.Length);

            // file format: header|kind|nonce|cipher|tag
            var fileBytes = new byte[HeaderSize + kindBytes.Length + nonce.Length + cipher.Length + tag.Length];
            var o = 0;
            header.CopyTo(fileBytes, o); o += header.Length;
            kindBytes.CopyTo(fileBytes, o); o += kindBytes.Length;
            nonce.CopyTo(fileBytes, o); o += nonce.Length;
            cipher.CopyTo(fileBytes, o); o += cipher.Length;
            tag.CopyTo(fileBytes, o);

            // The name is derived from the sequence alone: the tip is the only commit point, so if a
            // previous attempt at this sequence died before the tip was written, this write
            // overwrites it in place instead of leaving an orphan beside it. A record id in the name
            // would make every retry a new file.
            var path = RecordPath(seq);
            AtomicFile.WriteDurable(path, fileBytes);

            var newTip = new TipState
            {
                NextSequence = seq + 1,
                LastRecordId = record.RecordId,
                // Carry the enqueue watermark forward: Append commits the record, the caller enqueues
                // and then calls MarkEnqueued. Until it does, this record is unenqueued.
                LastEnqueuedSequence = tip.LastEnqueuedSequence ?? (seq - 1),
                UpdatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            WriteTipUnsafe(newTip);

            return new VaultAppendResult
            {
                Sequence = seq,
                FilePath = path
            };
        }
    }

    /// <summary>
    /// Records the highest sequence the caller has durably enqueued for upload. Advisory only, so it
    /// is never moved backward: a lost update just re-enqueues a record that <see cref="ReadUnenqueued"/>
    /// re-delivers idempotently (same RecordId → the server deduplicates).
    /// </summary>
    public void MarkEnqueued(long sequence)
    {
        lock (_gate)
        {
            var tip = ReadTipUnsafe();
            var current = tip.LastEnqueuedSequence ?? (tip.NextSequence - 1);
            if (sequence <= current)
            {
                return;
            }

            tip.LastEnqueuedSequence = sequence;
            WriteTipUnsafe(tip);
        }
    }

    /// <summary>
    /// Committed records that were never confirmed enqueued (§13.1): a crash between
    /// <see cref="Append"/> and the caller's enqueue commits a vault record that would otherwise
    /// never reach the server. Returns them oldest-first so the caller can re-enqueue and
    /// <see cref="MarkEnqueued"/> each.
    /// </summary>
    public IReadOnlyList<VaultPendingRecord> ReadUnenqueued()
    {
        lock (_gate)
        {
            var tip = ReadTipUnsafe();
            var lastEnqueued = tip.LastEnqueuedSequence ?? (tip.NextSequence - 1);
            var pending = new List<VaultPendingRecord>();
            for (var seq = lastEnqueued + 1; seq < tip.NextSequence; seq++)
            {
                var path = RecordPath(seq);
                if (!File.Exists(path))
                {
                    continue;
                }

                var record = TryDecryptRecord(path, seq);
                if (record is not null)
                {
                    pending.Add(record);
                }
            }

            return pending;
        }
    }

    private VaultPendingRecord? TryDecryptRecord(string path, long expectedSequence)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }

        if (bytes.Length < HeaderSize + NonceSize + TagSize)
        {
            return null;
        }

        var seq = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(0, 8));
        if (seq != expectedSequence)
        {
            return null;
        }

        var recordId = new Guid(bytes.AsSpan(8, 16));
        var occurredUnix = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(24, 8));
        var kindLen = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(32, 2));
        var offset = HeaderSize + kindLen;
        if (offset + NonceSize + TagSize > bytes.Length)
        {
            return null;
        }

        var cipherLen = bytes.Length - offset - NonceSize - TagSize;
        if (cipherLen < 0)
        {
            return null;
        }

        var kind = Encoding.UTF8.GetString(bytes.AsSpan(HeaderSize, kindLen));
        var nonce = bytes.AsSpan(offset, NonceSize).ToArray(); offset += NonceSize;
        var cipher = bytes.AsSpan(offset, cipherLen).ToArray(); offset += cipherLen;
        var tag = bytes.AsSpan(offset, TagSize).ToArray();

        byte[] plain;
        try
        {
            var compressed = new byte[cipher.Length];
            using var aes = new AesGcm(_encKey, TagSize);
            aes.Decrypt(nonce, cipher, tag, compressed);
            plain = BrotliDecompress(compressed);
        }
        catch (CryptographicException)
        {
            return null;
        }

        return new VaultPendingRecord
        {
            Sequence = seq,
            RecordId = recordId,
            Kind = kind,
            OccurredAt = DateTimeOffset.FromUnixTimeSeconds(occurredUnix),
            PlainPayload = plain
        };
    }

    /// <summary>
    /// Legacy tips predate <see cref="TipState.LastEnqueuedSequence"/>, and a rebuilt tip (see
    /// <see cref="ReadTipUnsafe"/>) leaves it null too. On the first open, assume everything already
    /// committed was uploaded so the upgrade does not re-send the whole history; from then on the
    /// watermark is tracked exactly.
    /// </summary>
    private void MigrateEnqueuedWatermark()
    {
        lock (_gate)
        {
            var tip = ReadTipUnsafe();
            if (tip.LastEnqueuedSequence is not null)
            {
                return;
            }

            tip.LastEnqueuedSequence = tip.NextSequence - 1;
            WriteTipUnsafe(tip);
        }
    }

    private string RecordPath(long sequence) => Path.Combine(_recordsDir, $"{sequence:D20}.tv1");

    /// <summary>
    /// Crash repair, run once at open.
    /// <para>
    /// Append writes the record and then the tip, so the tip is the commit point: sequences
    /// 1..NextSequence-1 are committed, and anything at or beyond NextSequence was written but never
    /// committed. Dropping the uncommitted remainder cannot lose committed data, and the orphan was
    /// never enqueued for upload either — enqueue happens after Append returns. Also clears
    /// half-written .tmp files.
    /// </para>
    /// <para>
    /// When the tip is absent or unreadable, NextSequence is rebuilt from the highest record
    /// filename, so no committed record is treated as uncommitted and deleted.
    /// </para>
    /// </summary>
    private void DiscardUncommittedRecords()
    {
        long nextSequence = ReadTipUnsafe().NextSequence;

        try
        {
            foreach (var path in Directory.EnumerateFiles(_recordsDir, "*.tv1*"))
            {
                var name = Path.GetFileName(path);
                var uncommitted = name.EndsWith(".tmp", StringComparison.Ordinal)
                                  || (TryReadSequenceFromName(name, out var seq) && seq >= nextSequence);
                if (!uncommitted)
                {
                    continue;
                }

                try { File.Delete(path); } catch (IOException) { /* in use — left for the next open */ }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // nothing written yet
        }
    }

    private static bool TryReadSequenceFromName(string fileName, out long sequence)
    {
        sequence = 0;
        return fileName.Length >= 20
               && long.TryParse(fileName.AsSpan(0, 20), out sequence);
    }

    /// <summary>
    /// The current tip. When the tip file is missing or unreadable, the commit counter is rebuilt
    /// from the records on disk (highest filename + 1) so a later <see cref="Append"/> never
    /// overwrites an existing record. Integrity of the commit point rests on the atomic rename, not
    /// on a MAC — tamper detection has been removed (§1.1).
    /// </summary>
    private TipState ReadTipUnsafe()
    {
        if (File.Exists(_tipPath))
        {
            try
            {
                var tip = JsonSerializer.Deserialize<TipState>(File.ReadAllBytes(_tipPath));
                if (tip is not null)
                {
                    return tip;
                }
            }
            catch (JsonException)
            {
                // Corrupt tip — rebuild the counter below rather than lose committed records.
            }
            catch (IOException)
            {
                // Transient read failure — rebuild below.
            }
        }

        return new TipState
        {
            NextSequence = HighestRecordSequence() + 1,
            LastRecordId = Guid.Empty,
            LastEnqueuedSequence = null,
            UpdatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }

    private long HighestRecordSequence()
    {
        long highest = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(_recordsDir, "*.tv1"))
            {
                if (TryReadSequenceFromName(Path.GetFileName(path), out var seq) && seq > highest)
                {
                    highest = seq;
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // nothing written yet
        }

        return highest;
    }

    private void WriteTipUnsafe(TipState tip)
        => AtomicFile.WriteDurable(_tipPath, JsonSerializer.SerializeToUtf8Bytes(tip));

    private static byte[] BrotliCompress(byte[] input)
    {
        using var ms = new MemoryStream();
        using (var brotli = new BrotliStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            brotli.Write(input);
        }

        return ms.ToArray();
    }

    private static byte[] BrotliDecompress(byte[] input)
    {
        using var source = new MemoryStream(input);
        using var brotli = new BrotliStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    private sealed class TipState
    {
        public long NextSequence { get; set; }
        public Guid LastRecordId { get; set; }

        /// <summary>Highest sequence the caller confirmed enqueued; null on legacy or rebuilt tips.</summary>
        public long? LastEnqueuedSequence { get; set; }
        public long UpdatedAtUnix { get; set; }
    }
}
