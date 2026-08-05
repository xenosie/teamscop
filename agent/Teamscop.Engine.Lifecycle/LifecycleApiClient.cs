using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Teamscop.Engine.Lifecycle;

public sealed class TotpEnrollResult
{
    public required string Secret { get; init; }
    public required string OtpAuthUri { get; init; }
    public required bool Enabled { get; init; }
}

public sealed class UninstallTicketResult
{
    public required string UninstallTicket { get; init; }
    public required long ExpiresIn { get; init; }
}

public sealed class LifecycleApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public LifecycleApiClient(string baseUrl, HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    }

    public async Task<TotpEnrollResult> EnrollTotpAsync(string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/lifecycle/totp/enroll");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<TotpEnrollResult>(JsonOptions, ct).ConfigureAwait(false))
               ?? throw new InvalidOperationException("Empty TOTP enroll response.");
    }

    public async Task<UninstallTicketResult> VerifyUninstallAsync(
        string deviceKey,
        string totpCode,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            "api/lifecycle/uninstall/verify",
            new { deviceKey, totpCode },
            JsonOptions,
            ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<UninstallTicketResult>(JsonOptions, ct).ConfigureAwait(false))
               ?? throw new InvalidOperationException("Empty uninstall verify response.");
    }

    public async Task HeartbeatAsync(string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/lifecycle/heartbeat");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new HttpRequestException($"Lifecycle API {(int)response.StatusCode}: {body}");
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
