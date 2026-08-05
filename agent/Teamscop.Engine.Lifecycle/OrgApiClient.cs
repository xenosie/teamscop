using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Teamscop.Engine.Lifecycle;

public sealed class OrgApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public OrgApiClient(string baseUrl, HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    }

    public async Task<OrgStructureDto> GetStructureAsync(string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/org/structure");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<OrgStructureDto>(JsonOptions, ct).ConfigureAwait(false))
               ?? throw new InvalidOperationException("Empty org structure.");
    }

    public async Task<MyOrgPlacementDto> GetMyPlacementAsync(string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/org/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<MyOrgPlacementDto>(JsonOptions, ct).ConfigureAwait(false))
               ?? throw new InvalidOperationException("Empty org placement.");
    }

    public async Task<TeamDto> CreateTeamAsync(
        string accessToken, string name, Guid leaderUserId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/teams");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = JsonContent.Create(new { name, leaderUserId }, options: JsonOptions);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<TeamDto>(JsonOptions, ct).ConfigureAwait(false))
               ?? throw new InvalidOperationException("Empty create team response.");
    }

    public async Task<TeamDto> UpdateTeamAsync(
        string accessToken, Guid teamId, string? name, Guid? leaderUserId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, $"api/teams/{teamId:D}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = JsonContent.Create(new { name, leaderUserId }, options: JsonOptions);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<TeamDto>(JsonOptions, ct).ConfigureAwait(false))
               ?? throw new InvalidOperationException("Empty update team response.");
    }

    public async Task DeleteTeamAsync(string accessToken, Guid teamId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"api/teams/{teamId:D}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
    }

    public async Task<TeamDto> SetMembersAsync(
        string accessToken, Guid teamId, IReadOnlyList<Guid> memberUserIds, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, $"api/teams/{teamId:D}/members");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = JsonContent.Create(new { memberUserIds }, options: JsonOptions);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<TeamDto>(JsonOptions, ct).ConfigureAwait(false))
               ?? throw new InvalidOperationException("Empty set members response.");
    }

    public async Task<TeamDto> AddMemberAsync(
        string accessToken, Guid teamId, Guid staffUserId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"api/teams/{teamId:D}/members");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = JsonContent.Create(new { staffUserId }, options: JsonOptions);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<TeamDto>(JsonOptions, ct).ConfigureAwait(false))
               ?? throw new InvalidOperationException("Empty add member response.");
    }

    public async Task<TeamDto> RemoveMemberAsync(
        string accessToken, Guid teamId, Guid staffUserId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"api/teams/{teamId:D}/members/{staffUserId:D}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<TeamDto>(JsonOptions, ct).ConfigureAwait(false))
               ?? throw new InvalidOperationException("Empty remove member response.");
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new HttpRequestException($"Org API {(int)response.StatusCode}: {body}");
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
