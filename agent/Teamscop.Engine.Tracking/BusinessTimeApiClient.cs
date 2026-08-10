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

/// <summary>
/// Business-time reads plus the admin timezone write. The write now targets
/// <c>PUT /api/business-time</c> (§8.4): picking a timezone replaces the whole clock, so the old
/// <c>POST /api/business-time/declare</c> is gone and 404s. Only <see cref="DeclareBusinessTimeBody.TimeZoneId"/>
/// is read server-side; the anchor fields are ignored remnants that retire with this client.
/// </summary>
public sealed class BusinessTimeApiClient : ApiClientBase
{
    public BusinessTimeApiClient(string baseUrl, HttpClient? httpClient = null)
        : base("BusinessTime API", baseUrl, httpClient)
    {
    }

    public async Task<BusinessClockConfig> GetMineAsync(string accessToken, CancellationToken ct = default)
        => await GetOrNullAsync<BusinessClockConfig>("api/business-time/me", accessToken, ct).ConfigureAwait(false)
           ?? throw new InvalidOperationException("Empty business-time config.");

    public async Task<BusinessTimeNowDto> GetNowAsync(string accessToken, CancellationToken ct = default)
        => await GetOrNullAsync<BusinessTimeNowDto>("api/business-time/now", accessToken, ct).ConfigureAwait(false)
           ?? throw new InvalidOperationException("Empty business-time now.");

    public async Task<BusinessClockConfig> DeclareAsync(
        string accessToken, DeclareBusinessTimeBody body, CancellationToken ct = default)
        => await SendJsonAsync<BusinessClockConfig>(HttpMethod.Put, "api/business-time", body, accessToken, ct)
               .ConfigureAwait(false)
           ?? throw new InvalidOperationException("Empty business-time response.");
}
