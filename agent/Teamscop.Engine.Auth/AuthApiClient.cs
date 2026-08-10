using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Teamscop.Engine.Auth;

public sealed class AuthApiClient : ApiClientBase
{
    public AuthApiClient(string baseUrl, HttpClient? httpClient = null)
        : base("Auth API", baseUrl, httpClient)
    {
    }

    public async Task<AuthSession> AdminSignupAsync(
        string deviceKey,
        string username,
        string password,
        Stream? avatarStream = null,
        string? avatarFileName = null,
        CancellationToken cancellationToken = default)
    {
        using var form = BuildSignupForm(deviceKey, username, password, companyToken: null, avatarStream, avatarFileName);
        using var response = await Http.PostAsync("api/auth/admin/signup", form, cancellationToken).ConfigureAwait(false);
        return await ReadSessionAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AuthSession> StaffSignupAsync(
        string deviceKey,
        string username,
        string password,
        string companyToken,
        Stream? avatarStream = null,
        string? avatarFileName = null,
        CancellationToken cancellationToken = default)
    {
        using var form = BuildSignupForm(deviceKey, username, password, companyToken, avatarStream, avatarFileName);
        using var response = await Http.PostAsync("api/auth/staff/signup", form, cancellationToken).ConfigureAwait(false);
        return await ReadSessionAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AuthSession> LoginAsync(string deviceKey, string password, CancellationToken cancellationToken = default)
    {
        using var response = await Http.PostAsJsonAsync(
            "api/auth/login",
            new LoginRequest { DeviceKey = deviceKey, Password = password },
            Json,
            cancellationToken).ConfigureAwait(false);
        return await ReadSessionAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AuthUser> MeAsync(string accessToken, CancellationToken cancellationToken = default)
        => await GetOrNullAsync<AuthUser>("api/auth/me", accessToken, cancellationToken).ConfigureAwait(false)
           ?? throw new InvalidOperationException("Empty /me response.");

    public async Task<string> RevealCompanyTokenAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Post, "api/auth/company-token/reveal", accessToken);
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("companyToken", out var tokenEl))
        {
            throw new InvalidOperationException("companyToken missing from reveal response.");
        }

        return tokenEl.GetString() ?? throw new InvalidOperationException("Empty companyToken.");
    }

    private static MultipartFormDataContent BuildSignupForm(
        string deviceKey,
        string username,
        string password,
        string? companyToken,
        Stream? avatarStream,
        string? avatarFileName)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(deviceKey), "deviceKey" },
            { new StringContent(username), "username" },
            { new StringContent(password), "password" }
        };

        if (!string.IsNullOrWhiteSpace(companyToken))
        {
            form.Add(new StringContent(companyToken), "companyToken");
        }

        if (avatarStream is not null)
        {
            var fileContent = new StreamContent(avatarStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "avatar", avatarFileName ?? "avatar.bin");
        }

        return form;
    }

    private async Task<AuthSession> ReadSessionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var session = await response.Content.ReadFromJsonAsync<AuthSession>(Json, cancellationToken).ConfigureAwait(false);
        return session ?? throw new InvalidOperationException("Empty auth session response.");
    }
}
