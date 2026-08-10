using Microsoft.Extensions.Options;
using Teamscop.Api.Options;

namespace Teamscop.Api.Services;

public interface IScreenshotBlobStorage
{
    Task WriteDisplayAsync(Guid userId, Guid eventId, int displayIndex, byte[] bytes, CancellationToken ct);
    Task<byte[]?> ReadDisplayAsync(Guid userId, Guid eventId, int displayIndex, CancellationToken ct);
    void DeleteEvent(Guid userId, Guid eventId);
}

/// <summary>
/// Screenshot blobs on disk (B4): <c>{root}/{userId:N}/{eventId:N}/d{displayIndex}.webp</c>.
/// The bytes are stored verbatim as the agent encodes them — WebP now (§3.1) — so no read path
/// pulls image bytes through PostgreSQL to answer "how many displays, how big".
/// </summary>
public sealed class ScreenshotBlobStorage(IOptions<StorageOptions> options, IHostEnvironment env) : IScreenshotBlobStorage
{
    private readonly string _root = ResolveRoot(options.Value, env);

    public async Task WriteDisplayAsync(Guid userId, Guid eventId, int displayIndex, byte[] bytes, CancellationToken ct)
    {
        var dir = Path.Combine(_root, userId.ToString("N"), eventId.ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"d{displayIndex}.webp");
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    public async Task<byte[]?> ReadDisplayAsync(Guid userId, Guid eventId, int displayIndex, CancellationToken ct)
    {
        var dir = Path.Combine(_root, userId.ToString("N"), eventId.ToString("N"));
        var webp = Path.Combine(dir, $"d{displayIndex}.webp");
        if (File.Exists(webp))
        {
            return await File.ReadAllBytesAsync(webp, ct).ConfigureAwait(false);
        }

        // Transition fallback: a capture stored by the pre-WebP path still serves.
        var jpg = Path.Combine(dir, $"d{displayIndex}.jpg");
        if (File.Exists(jpg))
        {
            return await File.ReadAllBytesAsync(jpg, ct).ConfigureAwait(false);
        }

        return null;
    }

    public void DeleteEvent(Guid userId, Guid eventId)
    {
        var dir = Path.Combine(_root, userId.ToString("N"), eventId.ToString("N"));
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static string ResolveRoot(StorageOptions storage, IHostEnvironment env)
    {
        var configured = string.IsNullOrWhiteSpace(storage.ScreenshotRoot)
            ? "data/screenshots"
            : storage.ScreenshotRoot;
        return Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(env.ContentRootPath, configured);
    }
}
