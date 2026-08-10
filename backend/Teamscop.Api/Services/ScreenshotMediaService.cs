using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using Teamscop.Api.Data;
using Teamscop.Api.Services.Access;
using Teamscop.Api.Services.Insights;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Services;

/// <summary>Image bytes plus the MIME type to serve them under.</summary>
public readonly record struct ScreenshotImage(byte[] Bytes, string ContentType);

public sealed class ScreenshotDisplayMetaDto
{
    public int Index { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Size { get; set; }
}

public sealed class ScreenshotMetaDto
{
    public Guid Id { get; set; }
    public Guid StaffUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Company-local wall clock (§8.2), computed at projection time. Kind is Unspecified.</summary>
    public DateTime? BusinessOccurredAt { get; set; }
    public string? BusinessTimeZoneId { get; set; }
    public int DisplayCount { get; set; }
    public List<ScreenshotDisplayMetaDto> Displays { get; set; } = [];
}

public interface IScreenshotMediaService
{
    /// <param name="before">
    /// Cursor for backwards paging: return only captures strictly older than this. The gallery
    /// passes the last tile's occurredAt, so pages cannot drift as new captures arrive.
    /// </param>
    Task<IReadOnlyList<ScreenshotMetaDto>> ListAsync(
        Guid viewerId,
        Guid staffUserId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        DateTimeOffset? before,
        int take,
        CancellationToken ct);

    /// <summary>A server-resized WebP thumbnail (§3.1) for the gallery grid, or null if not found.</summary>
    Task<ScreenshotImage?> GetThumbAsync(
        Guid viewerId,
        Guid eventId,
        int displayIndex,
        int maxWidth,
        CancellationToken ct);

    /// <summary>The stored capture bytes verbatim — no transcode, no generational loss (§3.4).</summary>
    Task<ScreenshotImage?> GetFullAsync(
        Guid viewerId,
        Guid eventId,
        int displayIndex,
        CancellationToken ct);
}

public sealed class ScreenshotMediaService(
    AppDbContext db,
    IStaffDataGuard guard,
    IScreenshotBlobStorage blobs) : IScreenshotMediaService
{
    private static readonly ConcurrentDictionary<string, byte[]> ThumbCache = new();
    private const int ThumbCacheMax = 256;

    /// <summary>Well past any real display wall; a 8K x 8K capture is already 64 MP.</summary>
    private const long MaxDecodePixels = 50_000_000;

    private const int MaxDecodeDimension = 20_000;

    private const string WebpMime = "image/webp";

    /// <summary>
    /// The MIME type for the stored bytes, sniffed from the magic number so the full image is served
    /// verbatim under the right type even during the WebP rollout (a leftover JPEG still serves).
    /// </summary>
    private static string DetectMime(byte[] bytes)
    {
        if (bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            return WebpMime;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        // Unknown — the store only ever holds agent-encoded WebP, so default to it.
        return WebpMime;
    }

    public async Task<IReadOnlyList<ScreenshotMetaDto>> ListAsync(
        Guid viewerId,
        Guid staffUserId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        DateTimeOffset? before,
        int take,
        CancellationToken ct)
    {
        await guard.RequireViewableAsync(viewerId, staffUserId, AgentEventTypes.ScreenshotMeta, ct);

        take = Math.Clamp(take <= 0 ? 100 : take, 1, 200);

        // `before` narrows the exclusive upper bound rather than replacing it, so a cursor can
        // never page outside the requested period.
        var upper = to;
        if (before is { } cursor)
        {
            var cursorUtc = cursor.ToUniversalTime();
            upper = upper is null || cursorUtc < upper ? cursorUtc : upper;
        }

        // PayloadJson on a screenshot row is metadata only — a few hundred bytes. Ingest moves the
        // JPEGs to blob storage before the row is written (B4), so this select can never drag an
        // image through PostgreSQL to answer "how many displays, how big".
        var rows = await db.AgentEvents.AsNoTracking()
            .ForStaff(staffUserId)
            .OfType(AgentEventTypes.ScreenshotMeta)
            .InPeriod(from, upper)
            .Newest(take)
            .Select(e => new
            {
                e.Id,
                e.UserId,
                e.OccurredAt,
                e.PayloadJson
            })
            .ToListAsync(ct);

        var timeZoneId = await db.Users.AsNoTracking()
            .Where(u => u.Id == staffUserId)
            .Select(u => u.Company.BusinessTimeZoneId)
            .FirstAsync(ct);
        var zone = CompanyBusinessTime.Resolve(timeZoneId);

        var list = new List<ScreenshotMetaDto>(rows.Count);
        foreach (var row in rows)
        {
            var displays = TryParseDisplayMeta(row.PayloadJson);
            list.Add(new ScreenshotMetaDto
            {
                Id = row.Id,
                StaffUserId = row.UserId,
                OccurredAt = row.OccurredAt,
                BusinessOccurredAt = CompanyBusinessTime.ToBusinessLocal(row.OccurredAt, zone),
                BusinessTimeZoneId = timeZoneId,
                DisplayCount = displays.Count,
                Displays = displays
            });
        }

        return list;
    }

    public async Task<ScreenshotImage?> GetThumbAsync(
        Guid viewerId,
        Guid eventId,
        int displayIndex,
        int maxWidth,
        CancellationToken ct)
    {
        maxWidth = Math.Clamp(maxWidth <= 0 ? 320 : maxWidth, 80, 640);
        displayIndex = displayIndex <= 0 ? 1 : displayIndex;

        var ownerId = await RequireOwnerAsync(viewerId, eventId, ct);
        if (ownerId is null)
        {
            return null;
        }

        var cacheKey = $"{eventId:D}:{displayIndex}:w{maxWidth}";
        if (ThumbCache.TryGetValue(cacheKey, out var cached))
        {
            return new ScreenshotImage(cached, WebpMime);
        }

        var stored = await blobs.ReadDisplayAsync(ownerId.Value, eventId, displayIndex, ct);
        if (stored is null || stored.Length == 0)
        {
            return null;
        }

        try
        {
            // B9 — read the header first and refuse before allocating. Checking megapixels after
            // Image.Load is checking after the allocation the check exists to prevent. Every agent
            // is a machine whose owner is a local admin, so a hostile image is a realistic input.
            var header = Image.Identify(stored);
            if (header.Width > MaxDecodeDimension
                || header.Height > MaxDecodeDimension
                || (long)header.Width * header.Height > MaxDecodePixels)
            {
                return null;
            }

            using var image = Image.Load(stored);
            if (image.Width > maxWidth)
            {
                var ratio = maxWidth / (double)image.Width;
                var h = Math.Max(1, (int)Math.Round(image.Height * ratio));
                image.Mutate(x => x.Resize(maxWidth, h));
            }

            // Re-encode WebP (§3.1) — roughly half the bytes of JPEG at the same visual quality.
            using var ms = new MemoryStream();
            await image.SaveAsWebpAsync(ms, new WebpEncoder { Quality = 72 }, ct).ConfigureAwait(false);
            var bytes = ms.ToArray();
            RememberThumb(cacheKey, bytes);
            return new ScreenshotImage(bytes, WebpMime);
        }
        catch
        {
            // Corrupt or undecodable — fall back to the stored bytes only if already small.
            if (stored.Length <= 24 * 1024)
            {
                return new ScreenshotImage(stored, DetectMime(stored));
            }

            return null;
        }
    }

    public async Task<ScreenshotImage?> GetFullAsync(
        Guid viewerId,
        Guid eventId,
        int displayIndex,
        CancellationToken ct)
    {
        displayIndex = displayIndex <= 0 ? 1 : displayIndex;
        var ownerId = await RequireOwnerAsync(viewerId, eventId, ct);
        if (ownerId is null)
        {
            return null;
        }

        var bytes = await blobs.ReadDisplayAsync(ownerId.Value, eventId, displayIndex, ct);
        return bytes is null ? null : new ScreenshotImage(bytes, DetectMime(bytes));
    }

    /// <summary>
    /// Who the capture belongs to, once the viewer has been cleared to see it. Only the owner is
    /// read from the row — the bytes live on disk, never in the payload (B4) — so the gallery pays
    /// one narrow indexed lookup per tile rather than dragging a payload back for each thumbnail.
    /// </summary>
    private async Task<Guid?> RequireOwnerAsync(Guid viewerId, Guid eventId, CancellationToken ct)
    {
        var ownerId = await db.AgentEvents.AsNoTracking()
            .OfType(AgentEventTypes.ScreenshotMeta)
            .Where(e => e.Id == eventId)
            .Select(e => (Guid?)e.UserId)
            .FirstOrDefaultAsync(ct);
        if (ownerId is null)
        {
            return null;
        }

        await guard.RequireViewableAsync(viewerId, ownerId.Value, AgentEventTypes.ScreenshotMeta, ct);
        return ownerId;
    }

    private static void RememberThumb(string key, byte[] bytes)
    {
        if (ThumbCache.Count >= ThumbCacheMax)
        {
            // Cheap eviction: drop an arbitrary entry (ConcurrentDictionary order not guaranteed).
            foreach (var k in ThumbCache.Keys.Take(32).ToList())
            {
                ThumbCache.TryRemove(k, out _);
            }
        }

        ThumbCache[key] = bytes;
    }

    private static List<ScreenshotDisplayMetaDto> TryParseDisplayMeta(string payloadJson)
    {
        var result = new List<ScreenshotDisplayMetaDto>();
        using var innerDoc = AgentEventPayload.TryOpen(payloadJson, "displays");
        if (innerDoc is null
            || !innerDoc.RootElement.TryGetProperty("displays", out var displays)
            || displays.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var d in displays.EnumerateArray())
        {
            var index = AgentEventPayload.Int(d, "DisplayIndex", "displayIndex", "index") ?? 0;
            result.Add(new ScreenshotDisplayMetaDto
            {
                Index = index <= 0 ? result.Count + 1 : index,
                Width = AgentEventPayload.Int(d, "Width", "width") ?? 0,
                Height = AgentEventPayload.Int(d, "Height", "height") ?? 0,
                Size = AgentEventPayload.Int(d, "size", "Size") ?? 0
            });
        }

        return result;
    }
}
