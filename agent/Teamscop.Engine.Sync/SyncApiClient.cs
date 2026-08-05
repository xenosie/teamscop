using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Teamscop.Engine.Sync;

public interface ISyncApiClient
{
    Task<IngestBatchResponse> PushBatchAsync(
        string accessToken,
        IReadOnlyList<OutboxItem> items,
        CancellationToken cancellationToken = default);
}

public sealed class SyncApiClient : ISyncApiClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public SyncApiClient(string baseUrl, HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
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

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/ingest/batch")
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await Teamscop.Engine.Auth.ApiClientException.ThrowIfUnsuccessfulAsync(
            response, "Ingest API", cancellationToken).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Deserialize<IngestBatchResponse>(raw, JsonOptions)
               ?? throw new InvalidOperationException("Empty ingest response.");
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
