using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Teamscop.App.Composition;
using Teamscop.App.Services;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.ViewModels;

public enum EnrollMode
{
    CreateCompany,
    JoinCompany
}

/// <summary>
/// Enrolment: register a business, or join one with a company token. Sign-in moved out to
/// <see cref="LoginViewModel"/> (§3.2) — the old AuthMode.SignIn value was never reachable.
/// </summary>
public sealed partial class EnrollViewModel : ObservableObject
{
    private readonly TeamscopApi _api;
    private readonly SessionStore _session;
    private readonly UiLog _log;
    private byte[]? _avatarPng;
    private AuthSession? _pendingSession;

    public EnrollViewModel(AppServices services)
    {
        _api = services.Api;
        _session = services.Session;
        _log = services.Log;
        Mode = EnrollMode.JoinCompany;

        // Same reason as LoginViewModel: deriving the device key runs several hardware queries,
        // and this view model is built inside the shell's constructor, so reading it synchronously
        // would block the UI thread before the first frame.
        LoadDeviceKeyAsync().FireAndForget(_log, "Device key");
    }

    private async Task LoadDeviceKeyAsync()
    {
        var key = await _session.DeviceKeyAsync().ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => DeviceKey = key);
    }

    public event Action<AuthSession>? Authenticated;

    /// <summary>"Already registered on this machine?" — hands back to the login screen.</summary>
    public event Action? SignInRequested;

    public Func<Task<(byte[] Png, Bitmap Preview)?>>? RequestPhotoCrop { get; set; }

    [ObservableProperty] private EnrollMode _mode;
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

    public bool IsCreateCompany => Mode == EnrollMode.CreateCompany;
    public bool IsJoinCompany => Mode == EnrollMode.JoinCompany;
    public bool HasAvatar => AvatarPreview is not null;

    partial void OnModeChanged(EnrollMode value)
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
        if (Enum.TryParse<EnrollMode>(modeName, ignoreCase: true, out var mode))
        {
            Mode = mode;
        }
    }

    [RelayCommand]
    private void ShowSignIn()
    {
        ErrorMessage = null;
        SignInRequested?.Invoke();
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
            ErrorMessage = ApiError.Describe(ex);
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

        if (!PasswordsValid("Admin password"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await using Stream avatarStream = new MemoryStream(_avatarPng);
            var session = await _api.AdminSignupAsync(
                DeviceKey,
                BusinessName.Trim(),
                Password,
                avatarStream,
                "business.png",
                CancellationToken.None);

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
            ErrorMessage = ApiError.Describe(ex);
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

        if (!PasswordsValid("Password"))
        {
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
            await using Stream avatarStream = new MemoryStream(_avatarPng);
            var session = await _api.StaffSignupAsync(
                DeviceKey,
                BusinessName.Trim(),
                Password,
                CompanyTokenInput.Trim(),
                avatarStream,
                "avatar.png",
                CancellationToken.None);

            // A Join that cannot persist is not a Join: the account exists on the server but this
            // machine forgets it on the next close, and the employee is left believing they are
            // enrolled. Do NOT clear the message PersistSafe just set, and do not proceed — that
            // combination turned a total failure into a clean-looking success.
            if (!PersistSafe(session, AgentRole.Staff))
            {
                return;
            }

            ErrorMessage = null;
            InfoMessage = null;
            Dispatcher.UIThread.Post(() => Authenticated?.Invoke(session));
        }
        catch (Exception ex)
        {
            ErrorMessage = ApiError.Describe(ex);
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
                    MainWindow.Clipboard: { } clipboard
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
            ErrorMessage = ApiError.Describe(ex);
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

    private bool PasswordsValid(string label)
    {
        if (string.IsNullOrWhiteSpace(Password) || Password.Length < 6)
        {
            ErrorMessage = $"{label} must be at least 6 characters.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(PasswordConfirm))
        {
            ErrorMessage = "Confirm password is required.";
            return false;
        }

        if (Password != PasswordConfirm)
        {
            ErrorMessage = "Passwords do not match.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Saves the session locally. Returns false when it could not be written — callers must not
    /// treat that as success. The staff path used to ignore the result and then clear the error
    /// message, so a machine that could not write its own token reported a clean Join.
    /// </summary>
    private bool PersistSafe(AuthSession session, AgentRole role)
    {
        try
        {
            _session.SaveFor(role, new LocalAgentState
            {
                AccessToken = session.AccessToken,
                DeviceKey = session.User.DeviceKey,
                Role = session.User.Role,
                UserId = session.User.Id,
                CompanyId = session.User.Company.Id,
                ApiBaseUrl = _session.ApiBaseUrl,
                Username = session.User.Username,
                CompanyName = session.User.Company.Name,
                CompanyAvatarUrl = session.User.Company.AvatarUrl
            });
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Enrolment succeeded but local state could not be saved", ex);
            ErrorMessage =
                "Signed up, but this machine could not save the session: " + ApiError.Describe(ex)
                + "\n\nThe account exists on the server. Re-run the installer as administrator so it "
                + "can create the session folder, then sign in.";
            return false;
        }
    }
}
