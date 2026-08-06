using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Teamscop.Engine.Auth;

namespace Teamscop.Engine.Tracking;

public sealed class DeclareBusinessTimeBody
{
    public string TimeZoneId { get; set; } = "UTC";
    public int Year { get; set; }
    public int Month { get; set; }
    public int Day { get; set; }
    public int Hour { get; set; }
    public int Minute { get; set; }
    public int Second { get; set; }
}

public sealed class BusinessTimeNowDto
{
    public Guid CompanyId { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public long ClockVersion { get; set; }
    public bool IsSynchronized { get; set; }
    public DateTimeOffset Utc { get; set; }
    public string BusinessLocal { get; set; } = "";
    public bool Synchronized { get; set; }
}

public sealed class BusinessTimeApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public BusinessTimeApiClient(string baseUrl, HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    }

    public async Task<BusinessClockConfig> GetMineAsync(string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/business-time/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await ApiClientException.ThrowIfUnsuccessfulAsync(resp, "BusinessTime API", ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<BusinessClockConfig>(JsonOptions, ct).ConfigureAwait(false))
               ?? throw new InvalidOperationException("Empty business-time config.");
    }

    public async Task<BusinessTimeNowDto> GetNowAsync(string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/business-time/now");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await ApiClientException.ThrowIfUnsuccessfulAsync(resp, "BusinessTime API", ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<BusinessTimeNowDto>(JsonOptions, ct).ConfigureAwait(false))
               ?? throw new InvalidOperationException("Empty business-time now.");
    }

    public async Task<BusinessClockConfig> DeclareAsync(
        string accessToken, DeclareBusinessTimeBody body, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/business-time/declare");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = JsonContent.Create(body, options: JsonOptions);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await ApiClientException.ThrowIfUnsuccessfulAsync(resp, "BusinessTime API", ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<BusinessClockConfig>(JsonOptions, ct).ConfigureAwait(false))
               ?? throw new InvalidOperationException("Empty declare response.");
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
