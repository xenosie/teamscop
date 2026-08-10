using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using Teamscop.Api.Data;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Tests;

/// <summary>
/// B4 — captures live on disk, and the row keeps only what a gallery listing needs. §3.1 — the wire
/// and the store are WebP now. The guard that matters is the stored payload: if an image ever
/// survives into <c>agent_events.PayloadJson</c>, every metadata listing silently starts dragging
/// megabytes through PostgreSQL again.
/// </summary>
public sealed class ScreenshotBlobFlowTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly string _blobRoot = Path.Combine(Path.GetTempPath(), "teamscop-blobs-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly TrackingScenario _api;

    public ScreenshotBlobFlowTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder => builder.UseSetting("Storage:ScreenshotRoot", _blobRoot));
        _api = new TrackingScenario(_factory.CreateClient());
    }

    [Fact]
    public async Task ACapture_IsStrippedToMetadata_AndServedFromTheBlobStore()
    {
        var (_, adminToken, companyToken) = await _api.SignupAdminAsync("Blob Co");
        var (staffId, staffToken) = await _api.SignupStaffAsync(companyToken, "Captured");

        var webp = new byte[4096];
        Random.Shared.NextBytes(webp);
        var occurredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _api.IngestAsync(staffToken, AgentEventTypes.ScreenshotMeta, occurredAt, JsonSerializer.Serialize(new
        {
            displays = new[]
            {
                new { displayIndex = 1, width = 1920, height = 1080, webpBase64 = Convert.ToBase64String(webp) }
            }
        }));

        var url = $"/api/tracking/screenshots?staffUserId={staffId:D}"
                  + $"&from={TrackingScenario.Iso(occurredAt.AddMinutes(-5))}"
                  + $"&to={TrackingScenario.Iso(occurredAt.AddMinutes(5))}";
        using var listing = await _api.GetJsonAsync(url, adminToken);
        var display = listing.RootElement[0].GetProperty("displays")[0];
        Assert.Equal(1920, display.GetProperty("width").GetInt32());
        Assert.Equal(1080, display.GetProperty("height").GetInt32());
        Assert.Equal(webp.Length, display.GetProperty("size").GetInt32());

        var eventId = listing.RootElement[0].GetProperty("id").GetGuid();
        var image = await _api.GetAsync($"/api/tracking/screenshots/{eventId:D}/image?display=1", adminToken);
        Assert.True(image.IsSuccessStatusCode);
        // The full image is served verbatim — no transcode, no generational loss (§3.4).
        Assert.Equal(webp, await image.Content.ReadAsByteArrayAsync());

        // The row itself must be metadata only — this is the whole point of B4.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.AgentEvents.AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => e.PayloadJson)
            .SingleAsync();
        Assert.DoesNotContain("webpBase64", stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("jpegBase64", stored, StringComparison.OrdinalIgnoreCase);
        Assert.True(stored.Length < 512, $"payload should be metadata only, was {stored.Length} chars");
    }

    [Fact]
    public async Task ARealWebpCapture_ServesAWebpThumbnailWithinBudget_AndTheFullBytesVerbatim()
    {
        var (_, adminToken, companyToken) = await _api.SignupAdminAsync("Webp Co");
        var (staffId, staffToken) = await _api.SignupStaffAsync(companyToken, "Shot");

        var webp = EncodeWebp(1280, 720);
        var occurredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _api.IngestAsync(staffToken, AgentEventTypes.ScreenshotMeta, occurredAt, JsonSerializer.Serialize(new
        {
            displays = new[]
            {
                new { displayIndex = 1, width = 1280, height = 720, webpBase64 = Convert.ToBase64String(webp) }
            }
        }));

        var url = $"/api/tracking/screenshots?staffUserId={staffId:D}"
                  + $"&from={TrackingScenario.Iso(occurredAt.AddMinutes(-5))}"
                  + $"&to={TrackingScenario.Iso(occurredAt.AddMinutes(5))}";
        using var listing = await _api.GetJsonAsync(url, adminToken);
        var eventId = listing.RootElement[0].GetProperty("id").GetGuid();

        // Thumbnail: server-resized, re-encoded WebP (§3.1).
        var thumb = await _api.GetAsync($"/api/tracking/screenshots/{eventId:D}/thumb?display=1&w=320", adminToken);
        Assert.True(thumb.IsSuccessStatusCode);
        Assert.Equal("image/webp", thumb.Content.Headers.ContentType?.MediaType);
        var thumbBytes = await thumb.Content.ReadAsByteArrayAsync();
        Assert.True(IsWebp(thumbBytes), "thumbnail must be a real WebP");
        Assert.True(thumbBytes.Length < 60 * 1024, $"a 320px thumbnail should be small, was {thumbBytes.Length}");

        // Full image: the stored WebP bytes verbatim, served as image/webp.
        var full = await _api.GetAsync($"/api/tracking/screenshots/{eventId:D}/image?display=1", adminToken);
        Assert.True(full.IsSuccessStatusCode);
        Assert.Equal("image/webp", full.Content.Headers.ContentType?.MediaType);
        Assert.Equal(webp, await full.Content.ReadAsByteArrayAsync());
    }

    private static byte[] EncodeWebp(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        // A little structure so the encoder produces a realistic, decodable frame.
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image[x, y] = new Rgba32((byte)(x % 256), (byte)(y % 256), (byte)((x + y) % 256));
            }
        }

        using var ms = new MemoryStream();
        image.SaveAsWebp(ms, new WebpEncoder { Quality = 72 });
        return ms.ToArray();
    }

    private static bool IsWebp(byte[] b)
        => b.Length >= 12
           && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
           && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P';

    public void Dispose()
    {
        if (Directory.Exists(_blobRoot))
        {
            Directory.Delete(_blobRoot, recursive: true);
        }
    }
}
