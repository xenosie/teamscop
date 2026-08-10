using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Teamscop.Engine.Auth;

/// <summary>
/// Shared core for every typed API client (C5). Owns the one JSON policy, bearer header, base-URL
/// construction, error mapping and disposal that all seven clients used to hand-roll. Clients keep
/// their existing <c>(string baseUrl, HttpClient? httpClient = null)</c> constructor so the single
/// shared <see cref="HttpClient"/> the desktop app funnels through is preserved unchanged.
/// </summary>
public abstract class ApiClientBase : IDisposable
{
    protected static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _apiName;
    private readonly bool _ownsClient;

    protected HttpClient Http { get; }

    protected ApiClientBase(string apiName, string baseUrl, HttpClient? shared, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Base URL is required.", nameof(baseUrl));
        }

        _apiName = apiName;
        _ownsClient = shared is null;
        Http = shared ?? new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        Http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    }

    protected Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
        => ApiClientException.ThrowIfUnsuccessfulAsync(response, _apiName, ct);

    protected HttpRequestMessage Request(HttpMethod method, string path, string? bearer)
    {
        var req = new HttpRequestMessage(method, path);
        if (!string.IsNullOrEmpty(bearer))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return req;
    }

    /// <summary>GET, deserialize; returns null only when the server sends a literal <c>null</c> body.</summary>
    protected async Task<T?> GetOrNullAsync<T>(string path, string? bearer, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, path, bearer);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        return await resp.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
    }

    /// <summary>GET, returning default(T) when the status is <paramref name="nullOn"/> (e.g. 403 without a package).</summary>
    protected async Task<T?> TryGetAsync<T>(string path, string? bearer, HttpStatusCode nullOn, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, path, bearer);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == nullOn)
        {
            return default;
        }

        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        return await resp.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
    }

    protected async Task<T?> SendJsonAsync<T>(
        HttpMethod method, string path, object? body, string? bearer, CancellationToken ct)
    {
        using var req = Request(method, path, bearer);
        if (body is not null)
        {
            req.Content = JsonContent.Create(body, options: Json);
        }

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        return await resp.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
    }

    protected async Task<byte[]> GetBytesAsync(string path, string? bearer, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, path, bearer);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds <c>?a=b&amp;c=d</c>: escapes keys and values, skips null values, and formats the
    /// agent's three recurring query types consistently — <see cref="DateTimeOffset"/> as a UTC
    /// round-trip string, <see cref="DateOnly"/> as yyyy-MM-dd, and <see cref="Guid"/> as D.
    /// Replaces nine hand-rolled from/to/take builders across the clients.
    /// </summary>
    protected static string Query(params (string Key, object? Value)[] parts)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in parts)
        {
            if (value is null)
            {
                continue;
            }

            var text = value switch
            {
                DateTimeOffset dto => dto.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Guid guid => guid.ToString("D"),
                bool flag => flag ? "true" : "false",
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty
            };

            sb.Append(sb.Length == 0 ? '?' : '&');
            sb.Append(Uri.EscapeDataString(key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(text));
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            Http.Dispose();
        }
    }
}
