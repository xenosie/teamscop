using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
    public required string ChainHashHex { get; init; }
    public required string FilePath { get; init; }
}

public sealed class VaultIntegrityReport
{
    public required bool Ok { get; init; }
    public required long ExpectedNextSequence { get; init; }
    public required long HighestSequenceFound { get; init; }
    public required int RecordCount { get; init; }
    public string? Error { get; init; }
    public bool TipMissing { get; init; }
    public bool ChainBreak { get; init; }
    public bool TamperedRecord { get; init; }
}

/// <summary>
/// Local tamper-evident vault: Brotli compress → AES-256-GCM encrypt → HMAC-SHA256 hash chain.
/// Designed for low CPU: small records, no full-folder scans on hot path (O(1) tip update).
/// </summary>
public sealed class SecureVault
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly string _recordsDir;
    private readonly string _tipPath;
    private readonly byte[] _encKey;
    private readonly byte[] _macKey;
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
        _macKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey32, 32, info: Encoding.UTF8.GetBytes("teamscop-vault-mac-v1"));
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

            var prev = Convert.FromHexString(tip.ChainHashHex);
            var header = new byte[8 + 16 + 8 + 2];
            BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(0, 8), seq);
            record.RecordId.ToByteArray().CopyTo(header, 8);
            BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(24, 8), record.OccurredAt.ToUnixTimeSeconds());
            var kindBytes = Encoding.UTF8.GetBytes(record.Kind);
            if (kindBytes.Length > 64)
            {
                throw new InvalidOperationException("Kind too long.");
            }

            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(32, 2), (ushort)kindBytes.Length);

            var chainInput = new byte[prev.Length + header.Length + kindBytes.Length + nonce.Length + cipher.Length + tag.Length];
            var o = 0;
            prev.CopyTo(chainInput, o); o += prev.Length;
            header.CopyTo(chainInput, o); o += header.Length;
            kindBytes.CopyTo(chainInput, o); o += kindBytes.Length;
            nonce.CopyTo(chainInput, o); o += nonce.Length;
            cipher.CopyTo(chainInput, o); o += cipher.Length;
            tag.CopyTo(chainInput, o);

            var chainHash = HMACSHA256.HashData(_macKey, chainInput);
            var fileBytes = new byte[chainInput.Length - prev.Length + 32 + 32];
            // file format: header|kind|nonce|cipher|tag|prevHash|chainHash
            o = 0;
            header.CopyTo(fileBytes, o); o += header.Length;
            kindBytes.CopyTo(fileBytes, o); o += kindBytes.Length;
            nonce.CopyTo(fileBytes, o); o += nonce.Length;
            cipher.CopyTo(fileBytes, o); o += cipher.Length;
            tag.CopyTo(fileBytes, o); o += tag.Length;
            prev.CopyTo(fileBytes, o); o += prev.Length;
            chainHash.CopyTo(fileBytes, o);

            var path = Path.Combine(_recordsDir, $"{seq:D20}_{record.RecordId:N}.tv1");
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, fileBytes);
            File.Move(tmp, path, overwrite: true);

            var newTip = new TipState
            {
                NextSequence = seq + 1,
                ChainHashHex = Convert.ToHexString(chainHash).ToLowerInvariant(),
                LastRecordId = record.RecordId,
                UpdatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            WriteTipUnsafe(newTip);

            return new VaultAppendResult
            {
                Sequence = seq,
                ChainHashHex = newTip.ChainHashHex,
                FilePath = path
            };
        }
    }

    public VaultIntegrityReport Verify(bool fullScan = true)
    {
        lock (_gate)
        {
            TipState tip;
            try
            {
                tip = ReadTipUnsafe();
            }
            catch (CryptographicException ex)
            {
                return new VaultIntegrityReport
                {
                    Ok = false,
                    ExpectedNextSequence = 1,
                    HighestSequenceFound = 0,
                    RecordCount = 0,
                    TipMissing = true,
                    Error = ex.Message
                };
            }

            if (!fullScan)
            {
                return new VaultIntegrityReport
                {
                    Ok = true,
                    ExpectedNextSequence = tip.NextSequence,
                    HighestSequenceFound = tip.NextSequence - 1,
                    RecordCount = -1
                };
            }

            var files = Directory.Exists(_recordsDir)
                ? Directory.GetFiles(_recordsDir, "*.tv1").OrderBy(f => f, StringComparer.Ordinal).ToArray()
                : [];

            if (files.Length == 0 && tip.NextSequence > 1)
            {
                return Fail(tip, 0, 0, "All vault records missing — possible mass deletion", chainBreak: true, tampered: true);
            }

            var prev = new byte[32]; // genesis
            long expected = 1;
            foreach (var file in files)
            {
                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(file);
                }
                catch (Exception ex)
                {
                    return Fail(tip, files.Length, expected - 1, $"Read failed: {ex.Message}", tampered: true);
                }

                if (bytes.Length < 8 + 16 + 8 + 2 + NonceSize + TagSize + 64)
                {
                    return Fail(tip, files.Length, expected - 1, "Truncated record", tampered: true);
                }

                var seq = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(0, 8));
                if (seq != expected)
                {
                    return Fail(tip, files.Length, Math.Max(expected - 1, seq), $"Sequence gap: expected {expected}, found {seq}", chainBreak: true);
                }

                var kindLen = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(32, 2));
                var offset = 34 + kindLen;
                if (offset + NonceSize + TagSize + 64 > bytes.Length)
                {
                    return Fail(tip, files.Length, seq, "Corrupt header", tampered: true);
                }

                var cipherLen = bytes.Length - offset - NonceSize - TagSize - 64;
                if (cipherLen < 0)
                {
                    return Fail(tip, files.Length, seq, "Corrupt body", tampered: true);
                }

                var kind = bytes.AsSpan(34, kindLen).ToArray();
                var nonce = bytes.AsSpan(offset, NonceSize).ToArray(); offset += NonceSize;
                var cipher = bytes.AsSpan(offset, cipherLen).ToArray(); offset += cipherLen;
                var tag = bytes.AsSpan(offset, TagSize).ToArray(); offset += TagSize;
                var storedPrev = bytes.AsSpan(offset, 32).ToArray(); offset += 32;
                var storedChain = bytes.AsSpan(offset, 32).ToArray();

                if (!CryptographicOperations.FixedTimeEquals(storedPrev, prev))
                {
                    return Fail(tip, files.Length, seq, "Prev-hash mismatch", chainBreak: true, tampered: true);
                }

                var header = bytes.AsSpan(0, 34).ToArray();
                var chainInput = new byte[prev.Length + header.Length + kind.Length + nonce.Length + cipher.Length + tag.Length];
                var o = 0;
                prev.CopyTo(chainInput, o); o += prev.Length;
                header.CopyTo(chainInput, o); o += header.Length;
                kind.CopyTo(chainInput, o); o += kind.Length;
                nonce.CopyTo(chainInput, o); o += nonce.Length;
                cipher.CopyTo(chainInput, o); o += cipher.Length;
                tag.CopyTo(chainInput, o);
                var calc = HMACSHA256.HashData(_macKey, chainInput);
                if (!CryptographicOperations.FixedTimeEquals(calc, storedChain))
                {
                    return Fail(tip, files.Length, seq, "HMAC chain mismatch", tampered: true);
                }

                // Decrypt to ensure ciphertext integrity (AES-GCM tag).
                try
                {
                    var plainCompressed = new byte[cipher.Length];
                    using var aes = new AesGcm(_encKey, TagSize);
                    aes.Decrypt(nonce, cipher, tag, plainCompressed);
                }
                catch (CryptographicException)
                {
                    return Fail(tip, files.Length, seq, "AES-GCM tag failed", tampered: true);
                }

                prev = storedChain;
                expected++;
            }

            if (tip.NextSequence != expected)
            {
                return Fail(tip, files.Length, expected - 1, $"Tip/sequence mismatch tip={tip.NextSequence} expectedNext={expected}", chainBreak: true);
            }

            if (!string.Equals(tip.ChainHashHex, Convert.ToHexString(prev).ToLowerInvariant(), StringComparison.Ordinal)
                && files.Length > 0)
            {
                return Fail(tip, files.Length, expected - 1, "Tip chain hash mismatch", tampered: true);
            }

            return new VaultIntegrityReport
            {
                Ok = true,
                ExpectedNextSequence = tip.NextSequence,
                HighestSequenceFound = tip.NextSequence - 1,
                RecordCount = files.Length
            };
        }
    }

    private VaultIntegrityReport Fail(TipState tip, int count, long highest, string error, bool chainBreak = false, bool tampered = false)
        => new()
        {
            Ok = false,
            ExpectedNextSequence = tip.NextSequence,
            HighestSequenceFound = highest,
            RecordCount = count,
            Error = error,
            ChainBreak = chainBreak,
            TamperedRecord = tampered
        };

    private TipState ReadTipUnsafe()
    {
        if (!File.Exists(_tipPath))
        {
            return new TipState
            {
                NextSequence = 1,
                ChainHashHex = Convert.ToHexString(new byte[32]).ToLowerInvariant(),
                LastRecordId = Guid.Empty,
                UpdatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
        }

        var raw = File.ReadAllBytes(_tipPath);
        if (raw.Length < 32 + 8)
        {
            throw new CryptographicException("Tip file truncated.");
        }

        var payload = raw.AsSpan(0, raw.Length - 32).ToArray();
        var mac = raw.AsSpan(raw.Length - 32).ToArray();
        var calc = HMACSHA256.HashData(_macKey, payload);
        if (!CryptographicOperations.FixedTimeEquals(mac, calc))
        {
            throw new CryptographicException("Tip HMAC invalid — possible tamper.");
        }

        var tip = JsonSerializer.Deserialize<TipState>(payload)
                  ?? throw new CryptographicException("Tip decode failed.");
        return tip;
    }

    private void WriteTipUnsafe(TipState tip)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(tip);
        var mac = HMACSHA256.HashData(_macKey, payload);
        var all = new byte[payload.Length + mac.Length];
        payload.CopyTo(all, 0);
        mac.CopyTo(all, payload.Length);
        var tmp = _tipPath + ".tmp";
        File.WriteAllBytes(tmp, all);
        File.Move(tmp, _tipPath, overwrite: true);
    }

    private static byte[] BrotliCompress(byte[] input)
    {
        using var ms = new MemoryStream();
        using (var brotli = new BrotliStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            brotli.Write(input);
        }

        return ms.ToArray();
    }

    private sealed class TipState
    {
        public long NextSequence { get; set; }
        public string ChainHashHex { get; set; } = "";
        public Guid LastRecordId { get; set; }
        public long UpdatedAtUnix { get; set; }
    }
}
