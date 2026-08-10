using System.Net.Http.Json;
using Teamscop.Engine.Auth;

namespace Teamscop.Engine.Sync;

public interface ISyncApiClient
{
    Task<IngestBatchResponse> PushBatchAsync(
        string accessToken,
        IReadOnlyList<OutboxItem> items,
        CancellationToken cancellationToken = default);
}

public sealed class SyncApiClient : ApiClientBase, ISyncApiClient
{
    public SyncApiClient(string baseUrl, HttpClient? httpClient = null)
        : base("Ingest API", baseUrl, httpClient)
    {
    }

    public async Task<IngestBatchResponse> PushBatchAsync(
        string accessToken,
        IReadOnlyList<OutboxItem> items,
        CancellationToken cancellationToken = default)
    {
        var body = new IngestBatchRequest
        {
            Events = items.Select(i => new IngestEventDto
            {
                ClientEventId = i.ClientEventId,
                EventType = i.EventType,
                OccurredAt = i.OccurredAt,
                PayloadJson = i.PayloadJson
            }).ToList()
        };

        return await SendGzippedJsonAsync<IngestBatchResponse>(
                   "api/ingest/batch", body, accessToken, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException("Empty ingest response.");
    }

    /// <summary>
    /// The ingest POST, gzip-compressed on the wire.
    ///
    /// Screenshots travel as base64 inside JSON, which inflates every WebP by a third — and this is
    /// by far the heaviest request the product makes, from every staff PC, all sharing the office's
    /// one uplink. Gzip collapses the base64 inflation back out (the WebP underneath is already
    /// compressed, but base64's 64-symbol alphabet is exactly what DEFLATE eats), so the batch goes
    /// over the wire at roughly the raw image size: about a 25% cut on the product's dominant
    /// traffic for one stream wrapper. The server's request-decompression middleware unwraps it;
    /// bodies without the header still work, so older agents are unaffected.
    /// </summary>
    private async Task<T?> SendGzippedJsonAsync<T>(
        string path, object body, string? bearer, CancellationToken ct)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(body, Json);
        using var buffer = new MemoryStream();
        await using (var gzip = new System.IO.Compression.GZipStream(
                         buffer, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            await gzip.WriteAsync(json, ct).ConfigureAwait(false);
        }

        using var req = Request(HttpMethod.Post, path, bearer);
        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        req.Content = content;

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        return await resp.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
    }
}
