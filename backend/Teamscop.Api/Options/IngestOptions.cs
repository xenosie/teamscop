namespace Teamscop.Api.Options;

/// <summary>
/// The four bounds on one ingest request (B10). They are configuration rather than constants so
/// the outer Kestrel body limit and the aggregate cap can be kept in step: Kestrel's
/// <c>MaxRequestBodySize</c> must stay above <see cref="MaxBatchBytes"/>, or the aggregate check
/// can never run and a large batch fails as a connection reset instead of a 400.
/// </summary>
public sealed class IngestOptions
{
    public const string SectionName = "Ingest";

    /// <summary>Events in one batch. The agent flushes far below this (§13.1 outbox drains FIFO).</summary>
    public int MaxBatchEvents { get; set; } = 200;

    /// <summary>Characters in one payload — a full-quality multi-display capture is the worst case.</summary>
    public int MaxPayloadChars { get; set; } = 2_000_000;

    /// <summary>Total payload bytes in one batch, checked before anything is parsed.</summary>
    public long MaxBatchBytes { get; set; } = 8L * 1024 * 1024;

    /// <summary>Kestrel's outer bound. Must exceed <see cref="MaxBatchBytes"/> plus envelope overhead.</summary>
    public long MaxRequestBodyBytes { get; set; } = 12L * 1024 * 1024;
}
