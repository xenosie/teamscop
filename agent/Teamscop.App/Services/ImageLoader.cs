using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Teamscop.App.Composition;

namespace Teamscop.App.Services;

/// <summary>
/// Avatars and thumbnails over the shared HttpClient, behind one LRU. Replaces the per-request
/// `new HttpClient()` avatar loaders, which fanned out one socket per staff member (§15.2).
/// </summary>
public sealed class ImageLoader : IDisposable
{
    /// <summary>
    /// Concurrent fetches. A page of 25 staff rows realising at once must not open 25 sockets on
    /// cheap hardware (§15.2); queuing behind four costs a few hundred milliseconds at worst.
    /// </summary>
    private const int MaxParallel = 4;

    private readonly HttpClient _http;
    private readonly SessionStore _session;
    private readonly UiLog _log;
    private readonly BitmapLruCache _cache = new(64);
    private readonly SemaphoreSlim _gate = new(MaxParallel, MaxParallel);
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _inFlight = new(StringComparer.Ordinal);

    public ImageLoader(HttpClient http, SessionStore session, UiLog log)
    {
        _http = http;
        _session = session;
        _log = log;
    }

    /// <summary>
    /// Null when the URL is missing or the image cannot be fetched — callers show their
    /// placeholder. A failed avatar is decoration, so it is logged at Debug and never surfaces.
    /// </summary>
    public async Task<Bitmap?> LoadAsync(string? url, CancellationToken ct = default)
    {
        var absolute = _session.ToAbsoluteUrl(url);
        if (absolute is null || ct.IsCancellationRequested)
        {
            return null;
        }

        if (_cache.TryGet(absolute, out var cached) && cached is not null)
        {
            return cached;
        }

        // One fetch per URL even when 50 rows ask at once. The entry is dropped after the await
        // so a failure is retried on the next request rather than cached as "no image".
        var fetch = _inFlight.GetOrAdd(absolute, key => FetchAsync(key, ct));
        try
        {
            return await fetch.ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(absolute, out _);
        }
    }

    public void Clear()
    {
        _inFlight.Clear();
        _cache.Clear();
    }

    public void Dispose() => Clear();

    /// <summary>
    /// Builds the avatar request. B12 put every media route behind <c>RequireAuthorization()</c>, so
    /// an anonymous GET — which is what this used to send — is a guaranteed 401 for EVERY avatar,
    /// including correctly-pathed ones. The bearer is attached per-request exactly as
    /// <c>ApiClientBase.Request</c> does; nothing is ever set on the shared HttpClient's default
    /// headers, because that client is also used for hosts this token has no business reaching.
    /// </summary>
    internal static HttpRequestMessage BuildRequest(string absolute, string? accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, absolute);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return request;
    }

    /// <summary>
    /// Is this response actually an image? A dead avatar path is answered by nginx's catch-all with
    /// HTTP 200 and a 24-byte <c>text/plain</c> banner, which reached the decoder as "success" and
    /// only failed by accident of Avalonia throwing. A missing Content-Type is accepted — the point
    /// is to reject something that positively claims not to be an image.
    /// </summary>
    internal static bool IsImageResponse(string? contentType)
        => string.IsNullOrWhiteSpace(contentType)
           || contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private async Task<Bitmap?> FetchAsync(string absolute, CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            byte[] bytes;
            try
            {
                using var request = BuildRequest(absolute, _session.AccessToken);
                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (!IsImageResponse(contentType))
                {
                    _log.Debug($"Image not loaded: {absolute} — server answered {contentType}, not an image");
                    return null;
                }

                bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            // Avalonia bitmaps are created on the UI thread so a decode failure cannot race a bind.
            var bitmap = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                using var stream = new MemoryStream(bytes);
                return new Bitmap(stream);
            });

            _cache.Set(absolute, bitmap);
            return bitmap;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or OperationCanceledException or ArgumentException or NotSupportedException)
        {
            _log.Debug($"Image not loaded: {absolute} — {ex.GetType().Name}");
            return null;
        }
    }
}
