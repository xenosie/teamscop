using System.Net.Http;
using System.Text.Json;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Teamscop.App.Services;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.ViewModels;

public enum AuthMode
{
    SignIn,
    CreateCompany,
    JoinCompany
}

public sealed partial class AuthViewModel : ObservableObject
{
    private readonly IDeviceKeyProvider _deviceKeys;
    private readonly LocalAgentStore _store;
    private readonly string _apiBaseUrl;
    private byte[]? _avatarPng;
    private AuthSession? _pendingSession;

    public AuthViewModel(
        IDeviceKeyProvider? deviceKeys = null,
        LocalAgentStore? store = null,
        string? apiBaseUrl = null)
    {
        _deviceKeys = deviceKeys ?? new DeviceKeyProvider();
        _store = store ?? new LocalAgentStore(AgentRole.Admin);
        var saved = _store.Load();
        _apiBaseUrl = apiBaseUrl
            ?? Environment.GetEnvironmentVariable("TEAMSCOP_API_BASE")
            ?? saved.ApiBaseUrl
            ?? "https://teamscop.com";

        DeviceKey = _deviceKeys.GetDeviceKey();
        Mode = AuthMode.JoinCompany;
    }

    public event Action<AuthSession>? Authenticated;
    public Func<Task<(byte[] Png, Bitmap Preview)?>>? RequestPhotoCrop { get; set; }

    [ObservableProperty] private AuthMode _mode;
    [ObservableProperty] private string _deviceKey = string.Empty;
    [ObservableProperty] private string _businessName = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _passwordConfirm = string.Empty;
    [ObservableProperty] private string _companyTokenInput = string.Empty;
    [ObservableProperty] private Bitmap? _avatarPreview;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _infoMessage;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _showTokenModal;
    [ObservableProperty] private string _issuedCompanyToken = string.Empty;
    [ObservableProperty] private bool _tokenCopied;

    public bool IsCreateCompany => Mode == AuthMode.CreateCompany;
    public bool IsJoinCompany => Mode == AuthMode.JoinCompany;
    public bool HasAvatar => AvatarPreview is not null;

    partial void OnModeChanged(AuthMode value)
    {
        ErrorMessage = null;
        InfoMessage = null;
        ShowTokenModal = false;
        OnPropertyChanged(nameof(IsCreateCompany));
        OnPropertyChanged(nameof(IsJoinCompany));
    }

    partial void OnAvatarPreviewChanged(Bitmap? value)
        => OnPropertyChanged(nameof(HasAvatar));

    [RelayCommand]
    private void SetMode(string? modeName)
    {
        if (Enum.TryParse<AuthMode>(modeName, ignoreCase: true, out var mode))
        {
            Mode = mode;
        }
    }

    [RelayCommand]
    private async Task PickPhotoAsync()
    {
        if (RequestPhotoCrop is null || IsBusy)
        {
            return;
        }

        ErrorMessage = null;
        try
        {
            var result = await RequestPhotoCrop.Invoke();
            if (result is null)
            {
                return;
            }

            _avatarPng = result.Value.Png;
            AvatarPreview = result.Value.Preview;
        }
        catch (Exception ex)
        {
            ErrorMessage = FormatError(ex);
        }
    }

    [RelayCommand]
    private void ClearPhoto()
    {
        _avatarPng = null;
        AvatarPreview = null;
    }

    [RelayCommand]
    private async Task CreateBusinessAsync()
    {
        if (IsBusy || !IsCreateCompany)
        {
            return;
        }

        ErrorMessage = null;
        InfoMessage = null;

        if (_avatarPng is null || AvatarPreview is null)
        {
            ErrorMessage = "Business photo is required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(BusinessName))
        {
            ErrorMessage = "Business name is required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Password) || Password.Length < 6)
        {
            ErrorMessage = "Admin password must be at least 6 characters.";
            return;
        }

        if (string.IsNullOrWhiteSpace(PasswordConfirm))
        {
            ErrorMessage = "Confirm password is required.";
            return;
        }

        if (Password != PasswordConfirm)
        {
            ErrorMessage = "Passwords do not match.";
            return;
        }

        IsBusy = true;
        try
        {
            using var api = new AuthApiClient(_apiBaseUrl);
            await using Stream avatarStream = new MemoryStream(_avatarPng);
            var session = await api.AdminSignupAsync(
                DeviceKey,
                BusinessName.Trim(),
                Password,
                avatarStream,
                "business.png");

            if (string.IsNullOrWhiteSpace(session.CompanyToken))
            {
                ErrorMessage = "Business created, but no token was returned.";
                PersistSafe(session, AgentRole.Admin);
                return;
            }

            PersistSafe(session, AgentRole.Admin);
            _pendingSession = session;
            IssuedCompanyToken = session.CompanyToken;
            TokenCopied = false;
            ShowTokenModal = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = FormatError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task JoinBusinessAsync()
    {
        if (IsBusy || !IsJoinCompany)
        {
            return;
        }

        ErrorMessage = null;
        InfoMessage = null;

        if (_avatarPng is null || AvatarPreview is null)
        {
            ErrorMessage = "Your photo is required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(BusinessName))
        {
            ErrorMessage = "Your name is required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Password) || Password.Length < 6)
        {
            ErrorMessage = "Password must be at least 6 characters.";
            return;
        }

        if (string.IsNullOrWhiteSpace(PasswordConfirm))
        {
            ErrorMessage = "Confirm password is required.";
            return;
        }

        if (Password != PasswordConfirm)
        {
            ErrorMessage = "Passwords do not match.";
            return;
        }

        if (string.IsNullOrWhiteSpace(CompanyTokenInput))
        {
            ErrorMessage = "Business token is required.";
            return;
        }

        IsBusy = true;
        try
        {
            using var api = new AuthApiClient(_apiBaseUrl);
            await using Stream avatarStream = new MemoryStream(_avatarPng);
            var session = await api.StaffSignupAsync(
                DeviceKey,
                BusinessName.Trim(),
                Password,
                CompanyTokenInput.Trim(),
                avatarStream,
                "avatar.png");

            PersistSafe(session, AgentRole.Staff);
            ErrorMessage = null;
            InfoMessage = null;
            Dispatcher.UIThread.Post(() => Authenticated?.Invoke(session));
        }
        catch (Exception ex)
        {
            ErrorMessage = FormatError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CopyTokenAsync()
    {
        if (string.IsNullOrEmpty(IssuedCompanyToken))
        {
            return;
        }

        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow: { Clipboard: { } clipboard }
                })
            {
                await clipboard.SetTextAsync(IssuedCompanyToken);
                TokenCopied = true;
            }
            else
            {
                ErrorMessage = "Could not copy token.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = FormatError(ex);
        }
    }

    [RelayCommand]
    private void ContinueAfterToken()
    {
        ShowTokenModal = false;
        if (_pendingSession is not null)
        {
            var session = _pendingSession;
            _pendingSession = null;
            Dispatcher.UIThread.Post(() => Authenticated?.Invoke(session));
        }
    }

    private void PersistSafe(AuthSession session, AgentRole role)
    {
        try
        {
            var store = role == AgentRole.Staff ? new LocalAgentStore(AgentRole.Staff) : _store;
            var state = store.Load();
            state.AccessToken = session.AccessToken;
            state.DeviceKey = session.User.DeviceKey;
            state.Role = session.User.Role;
            state.UserId = session.User.Id;
            state.CompanyId = session.User.Company.Id;
            state.ApiBaseUrl = _apiBaseUrl;
            state.Username = session.User.Username;
            state.CompanyName = session.User.Company.Name;
            state.CompanyAvatarUrl = session.User.Company.AvatarUrl;
            store.Save(state);
            AppSessionStore.SetActive(role);
        }
        catch (Exception ex)
        {
            // Account may already exist on the server — keep going, surface save failure.
            ErrorMessage = "Signed up, but local save failed: " + FormatError(ex);
        }
    }

    private static string FormatError(Exception ex)
    {
        ex = Unwrap(ex);

        switch (ex)
        {
            case ApiClientException api:
                return CleanApiMessage(api.Message);
            case HttpRequestException http when http.Message.StartsWith("Auth API error ", StringComparison.Ordinal)
                || http.Message.StartsWith("Auth API:", StringComparison.Ordinal):
                return ExtractApiError(http.Message);
            case HttpRequestException:
                return "Could not reach the server.";
            case TaskCanceledException:
                return "Request timed out.";
            case JsonException:
                return "Invalid server response.";
            case IOException:
                return "File or storage error.";
            case UnauthorizedAccessException:
                return string.IsNullOrWhiteSpace(ex.Message) ? "Not allowed." : ex.Message;
            case InvalidOperationException when !string.IsNullOrWhiteSpace(ex.Message):
                return ex.Message;
            default:
                var msg = ExtractApiError(ex.Message);
                return string.IsNullOrWhiteSpace(msg) ? "Something went wrong." : msg;
        }
    }

    private static string CleanApiMessage(string message)
    {
        foreach (var prefix in new[] { "Auth API: ", "Org API: ", "Lifecycle API: ", "Ingest API: ", "Police API: " })
        {
            if (message.StartsWith(prefix, StringComparison.Ordinal))
            {
                return message[prefix.Length..];
            }
        }

        return ExtractApiError(message);
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is AggregateException { InnerException: { } inner })
        {
            ex = inner;
        }

        return ex.InnerException is ApiClientException or HttpRequestException or TaskCanceledException or IOException
            ? ex.InnerException
            : ex;
    }

    private static string ExtractApiError(string message)
    {
        const string marker = "Auth API error ";
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        if (!message.StartsWith(marker, StringComparison.Ordinal))
        {
            return message;
        }

        var bodyStart = message.IndexOf(':');
        if (bodyStart < 0 || bodyStart + 1 >= message.Length)
        {
            return message;
        }

        var body = message[(bodyStart + 1)..].Trim();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var errorEl))
            {
                var text = errorEl.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }
        catch (JsonException)
        {
            // not JSON — fall through
        }

        return body;
    }
}
