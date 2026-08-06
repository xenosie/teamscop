using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using Teamscop.Api.Data;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Services;

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
    public DateTime? BusinessOccurredAt { get; set; }
    public string? BusinessTimeZoneId { get; set; }
    public int DisplayCount { get; set; }
    public List<ScreenshotDisplayMetaDto> Displays { get; set; } = [];
}

public interface IScreenshotMediaService
{
    Task<IReadOnlyList<ScreenshotMetaDto>> ListAsync(
        Guid viewerId,
        Guid staffUserId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int take,
        CancellationToken ct);

    Task<byte[]?> GetThumbJpegAsync(
        Guid viewerId,
        Guid eventId,
        int displayIndex,
        int maxWidth,
        CancellationToken ct);

    Task<byte[]?> GetFullJpegAsync(
        Guid viewerId,
        Guid eventId,
        int displayIndex,
        CancellationToken ct);
}

public sealed class ScreenshotMediaService(
    AppDbContext db,
    IAuthorityService authorities) : IScreenshotMediaService
{
    private static readonly ConcurrentDictionary<string, byte[]> ThumbCache = new();
    private const int ThumbCacheMax = 256;

    public async Task<IReadOnlyList<ScreenshotMetaDto>> ListAsync(
        Guid viewerId,
        Guid staffUserId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int take,
        CancellationToken ct)
    {
        await EnsureCanViewAsync(viewerId, staffUserId, ct);

        take = Math.Clamp(take <= 0 ? 100 : take, 1, 200);
        var q = db.AgentEvents.AsNoTracking()
            .Where(e => e.UserId == staffUserId && e.EventType == AgentEventTypes.ScreenshotMeta);
        if (from is not null)
        {
            q = q.Where(e => e.OccurredAt >= from);
        }

        if (to is not null)
        {
            q = q.Where(e => e.OccurredAt < to);
        }

        // Select only identity + small payload fields needed to parse display meta.
        // Avoid shipping jpegBase64 to the client: strip after parse.
        var rows = await q.OrderByDescending(e => e.OccurredAt)
            .Take(take)
            .Select(e => new
            {
                e.Id,
                e.UserId,
                e.OccurredAt,
                e.BusinessOccurredAt,
                e.BusinessTimeZoneId,
                e.PayloadJson
            })
            .ToListAsync(ct);

        var list = new List<ScreenshotMetaDto>(rows.Count);
        foreach (var row in rows)
        {
            var displays = TryParseDisplayMeta(row.PayloadJson);
            list.Add(new ScreenshotMetaDto
            {
                Id = row.Id,
                StaffUserId = row.UserId,
                OccurredAt = row.OccurredAt,
                BusinessOccurredAt = row.BusinessOccurredAt,
                BusinessTimeZoneId = row.BusinessTimeZoneId,
                DisplayCount = displays.Count,
                Displays = displays
            });
        }

        return list;
    }

    public async Task<byte[]?> GetThumbJpegAsync(
        Guid viewerId,
        Guid eventId,
        int displayIndex,
        int maxWidth,
        CancellationToken ct)
    {
        maxWidth = Math.Clamp(maxWidth <= 0 ? 320 : maxWidth, 80, 640);
        displayIndex = displayIndex <= 0 ? 1 : displayIndex;
        var cacheKey = $"{eventId:D}:{displayIndex}:w{maxWidth}";
        if (ThumbCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var jpeg = await LoadDisplayJpegAsync(viewerId, eventId, displayIndex, ct);
        if (jpeg is null || jpeg.Length == 0)
        {
            return null;
        }

        try
        {
            using var image = Image.Load(jpeg);
            if (image.Width > maxWidth)
            {
                var ratio = maxWidth / (double)image.Width;
                var h = Math.Max(1, (int)Math.Round(image.Height * ratio));
                image.Mutate(x => x.Resize(maxWidth, h));
            }

            using var ms = new MemoryStream();
            await image.SaveAsJpegAsync(ms, new JpegEncoder { Quality = 55 }, ct).ConfigureAwait(false);
            var bytes = ms.ToArray();
            RememberThumb(cacheKey, bytes);
            return bytes;
        }
        catch
        {
            // Corrupt or non-JPEG — fall back to original only if already small.
            if (jpeg.Length <= 24 * 1024)
            {
                RememberThumb(cacheKey, jpeg);
                return jpeg;
            }

            return null;
        }
    }

    public async Task<byte[]?> GetFullJpegAsync(
        Guid viewerId,
        Guid eventId,
        int displayIndex,
        CancellationToken ct)
    {
        displayIndex = displayIndex <= 0 ? 1 : displayIndex;
        return await LoadDisplayJpegAsync(viewerId, eventId, displayIndex, ct);
    }

    private async Task<byte[]?> LoadDisplayJpegAsync(
        Guid viewerId,
        Guid eventId,
        int displayIndex,
        CancellationToken ct)
    {
        var row = await db.AgentEvents.AsNoTracking()
            .Where(e => e.Id == eventId && e.EventType == AgentEventTypes.ScreenshotMeta)
            .Select(e => new { e.UserId, e.PayloadJson })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }

        await EnsureCanViewAsync(viewerId, row.UserId, ct);
        return TryExtractJpeg(row.PayloadJson, displayIndex);
    }

    private async Task EnsureCanViewAsync(Guid viewerId, Guid staffUserId, CancellationToken ct)
    {
        if (!await authorities.CanViewStaffAsync(viewerId, staffUserId, ct))
        {
            throw new UnauthorizedAccessException("Not allowed to view this staff member's tracking data.");
        }

        if (!await authorities.CanViewEventTypeAsync(viewerId, AgentEventTypes.ScreenshotMeta, ct))
        {
            throw new UnauthorizedAccessException("Missing authority package for screenshots.");
        }
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
        try
        {
            using var innerDoc = OpenInnerDocument(payloadJson);
            if (innerDoc is null)
            {
                return result;
            }

            var inner = innerDoc.RootElement;
            if (!inner.TryGetProperty("displays", out var displays)
                || displays.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var d in displays.EnumerateArray())
            {
                var index = ReadInt(d, "DisplayIndex", "displayIndex", "index") ?? 0;
                var width = ReadInt(d, "Width", "width") ?? 0;
                var height = ReadInt(d, "Height", "height") ?? 0;
                var size = ReadInt(d, "size", "Size") ?? 0;
                if (size <= 0
                    && d.TryGetProperty("jpegBase64", out var b64)
                    && b64.ValueKind == JsonValueKind.String)
                {
                    var s = b64.GetString();
                    if (!string.IsNullOrEmpty(s))
                    {
                        // Approximate decoded size from base64 length.
                        size = (int)(s.Length * 3L / 4);
                    }
                }

                result.Add(new ScreenshotDisplayMetaDto
                {
                    Index = index <= 0 ? result.Count + 1 : index,
                    Width = width,
                    Height = height,
                    Size = size
                });
            }
        }
        catch (JsonException)
        {
            // ignore malformed
        }

        return result;
    }

    private static byte[]? TryExtractJpeg(string payloadJson, int displayIndex)
    {
        try
        {
            using var innerDoc = OpenInnerDocument(payloadJson);
            if (innerDoc is null)
            {
                return null;
            }

            var inner = innerDoc.RootElement;
            if (!inner.TryGetProperty("displays", out var displays)
                || displays.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            JsonElement? match = null;
            var i = 0;
            foreach (var d in displays.EnumerateArray())
            {
                i++;
                var idx = ReadInt(d, "DisplayIndex", "displayIndex", "index") ?? i;
                if (idx == displayIndex)
                {
                    match = d;
                    break;
                }
            }

            match ??= displays.GetArrayLength() > 0 ? displays[0] : null;
            if (match is null)
            {
                return null;
            }

            if (!match.Value.TryGetProperty("jpegBase64", out var b64El)
                || b64El.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var b64 = b64El.GetString();
            if (string.IsNullOrWhiteSpace(b64))
            {
                return null;
            }

            return Convert.FromBase64String(b64);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Returns a document for the inner screenshot payload (caller must dispose).</summary>
    private static JsonDocument? OpenInnerDocument(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        using var outer = JsonDocument.Parse(payloadJson);
        var root = outer.RootElement;
        if (root.TryGetProperty("payloadBase64", out var pb)
            && pb.ValueKind == JsonValueKind.String)
        {
            var b64 = pb.GetString();
            if (string.IsNullOrWhiteSpace(b64))
            {
                return null;
            }

            try
            {
                return JsonDocument.Parse(Convert.FromBase64String(b64));
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                return null;
            }
        }

        // Already-unwrapped / test payloads — re-parse owned copy.
        if (root.TryGetProperty("displays", out _))
        {
            return JsonDocument.Parse(payloadJson);
        }

        return null;
    }

    private static int? ReadInt(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.TryGetProperty(name, out var p))
            {
                if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n))
                {
                    return n;
                }

                if (p.ValueKind == JsonValueKind.String
                    && int.TryParse(p.GetString(), out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }
}
