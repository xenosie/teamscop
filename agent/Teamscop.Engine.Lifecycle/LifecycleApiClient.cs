using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Teamscop.Engine.Lifecycle;

public sealed class TotpEnrollResult
{
    public required Guid StaffUserId { get; init; }
    public required string StaffUsername { get; init; }
    public required string Secret { get; init; }
    public required string OtpAuthUri { get; init; }
    public required bool Enabled { get; init; }
}

public sealed class TotpStatusResult
{
    public required Guid StaffUserId { get; init; }
    public required string StaffUsername { get; init; }
    public required bool Enabled { get; init; }
    public DateTimeOffset? EnrolledAt { get; init; }

    /// <summary>"admin" | "staff". The admin's own row appears only for the admin themselves.</summary>
    public string? Role { get; init; }

    /// <summary>
    /// This row is the caller's own machine. It exists so the admin can enrol the PC that carries
    /// the uninstall guard; it accepts the uninstall purpose only, never USB (§9.4 gates monitored
    /// machines, not the admin console).
    /// </summary>
    public bool IsSelfMachine { get; init; }
}

public sealed class TotpCodeResult
{
    public required Guid StaffUserId { get; init; }
    public required string StaffUsername { get; init; }
    public required string Code { get; init; }
    public required int PeriodSeconds { get; init; }
    public required int RemainingSeconds { get; init; }
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

    public async Task<TotpEnrollResult> EnrollTotpAsync(
        string accessToken,
        Guid staffUserId,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/lifecycle/totp/enroll");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = JsonContent.Create(new { staffUserId }, options: JsonOptions);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<TotpEnrollResult>(JsonOptions, ct).ConfigureAwait(false))
               ?? throw new InvalidOperationException("Empty TOTP enroll response.");
    }

    public async Task<IReadOnlyList<TotpStatusResult>> ListStaffTotpAsync(
        string accessToken,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/lifecycle/totp/staff");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<List<TotpStatusResult>>(JsonOptions, ct).ConfigureAwait(false))
               ?? [];
    }

    public async Task<TotpCodeResult> GetTotpCodeAsync(
        string accessToken,
        Guid staffUserId,
        CancellationToken ct = default,
        string? purpose = null)
    {
        var url = $"api/lifecycle/totp/code/{staffUserId:D}";
        if (!string.IsNullOrWhiteSpace(purpose))
        {
            url += "?purpose=" + Uri.EscapeDataString(purpose.Trim());
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<TotpCodeResult>(JsonOptions, ct).ConfigureAwait(false))
               ?? throw new InvalidOperationException("Empty TOTP code response.");
    }

    public Task HeartbeatAsync(string accessToken, CancellationToken ct = default)
        => HeartbeatAsync(accessToken, null, ct);

    /// <summary>
    /// The service's liveness ping, carrying the agent's own verdict on capture and on the files
    /// under its install directory.
    ///
    /// The verdict travels here rather than through the outbox deliberately: the outbox is a FIFO
    /// queue that can be hundreds of items deep after an outage, so health news would arrive last —
    /// precisely when it matters most.
    /// </summary>
    public async Task HeartbeatAsync(
        string accessToken,
        AgentHealthReport? report,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/lifecycle/heartbeat");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (report is not null)
        {
            req.Content = JsonContent.Create(report, options: JsonOptions);
        }

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The desktop app's independent report. Its own route so it can never be mistaken for the
    /// service's liveness — this one runs as the monitored user.
    /// </summary>
    public async Task AppReportAsync(
        string accessToken,
        AppStatusReport report,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/lifecycle/app-report");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = JsonContent.Create(report, options: JsonOptions);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
    }

    private static Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
        => Teamscop.Engine.Auth.ApiClientException.ThrowIfUnsuccessfulAsync(response, "Lifecycle API", ct);

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}

/// <summary>What the service tells the server about its own health, on every heartbeat.</summary>
public sealed record AgentHealthReport(
    string? CaptureState,
    string? CaptureReason,
    string? MissingComponents);

/// <summary>What the desktop app tells the server about the service and the files on disk.</summary>
public sealed record AppStatusReport(string? ServiceState, string? MissingComponents);
