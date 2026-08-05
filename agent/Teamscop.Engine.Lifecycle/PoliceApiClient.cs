using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Teamscop.Engine.Lifecycle;

public sealed class PoliceApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public PoliceApiClient(string baseUrl, HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    }

    public async Task<IReadOnlyList<AuthorityPackageInfo>> ListPackagesAsync(
        string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/police/packages");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<List<AuthorityPackageInfo>>(JsonOptions, ct).ConfigureAwait(false))
               ?? [];
    }

    public async Task<EffectiveAuthoritiesDto> GetMineAsync(string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/police/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<EffectiveAuthoritiesDto>(JsonOptions, ct).ConfigureAwait(false))
               ?? throw new InvalidOperationException("Empty authorities response.");
    }

    public async Task<IReadOnlyList<PolicemanDto>> ListPolicemenAsync(string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/police");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<List<PolicemanDto>>(JsonOptions, ct).ConfigureAwait(false))
               ?? [];
    }

    public async Task<PolicemanDto> UpsertPolicemanAsync(
        string accessToken, Guid staffUserId, IReadOnlyList<string> packages, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, $"api/police/{staffUserId:D}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = JsonContent.Create(new { packages }, options: JsonOptions);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<PolicemanDto>(JsonOptions, ct).ConfigureAwait(false))
               ?? throw new InvalidOperationException("Empty policeman response.");
    }

    public async Task RevokePolicemanAsync(string accessToken, Guid staffUserId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"api/police/{staffUserId:D}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new HttpRequestException($"Police API {(int)response.StatusCode}: {body}");
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
