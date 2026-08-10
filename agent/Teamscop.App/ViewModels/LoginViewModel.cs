using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Teamscop.App.Composition;
using Teamscop.App.Services;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;
using Avalonia.Threading;

namespace Teamscop.App.ViewModels;

/// <summary>
/// A13 / §3.2 — the login screen that did not exist. The credential is device key + password;
/// the device key is derived from this machine and shown read-only, so the user types a password
/// and nothing else. Only leaders, policemen and admins ever need it.
/// </summary>
public sealed partial class LoginViewModel : ObservableObject
{
    private readonly TeamscopApi _api;
    private readonly SessionStore _session;
    private readonly UiLog _log;

    public LoginViewModel(AppServices services)
    {
        _api = services.Api;
        _session = services.Session;
        _log = services.Log;

        // Deriving the device key spawns several hardware queries. Reading it here synchronously
        // blocked the UI thread before the first frame, because this view model is built inside
        // the shell's constructor. Fill it in once it arrives.
        _ = LoadDeviceKeyAsync();
    }

    private async Task LoadDeviceKeyAsync()
    {
        var key = await _session.DeviceKeyAsync().ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => DeviceKey = key);
    }

    public event Action<AuthSession>? Authenticated;

    /// <summary>"Not registered on this machine?" — hands over to the enrolment screens.</summary>
    public event Action? EnrollRequested;

    [ObservableProperty] private string _deviceKey = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;

    public bool HasDeviceKey => !string.IsNullOrWhiteSpace(DeviceKey);
    public bool ShowError => !string.IsNullOrWhiteSpace(ErrorMessage);

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = null;

        if (!HasDeviceKey)
        {
            ErrorMessage = "This machine's device key could not be read. Reinstall the agent.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Password is required.";
            return;
        }

        IsBusy = true;
        try
        {
            var session = await _api.LoginAsync(DeviceKey, Password, CancellationToken.None);
            Persist(session);
            Password = string.Empty;
            Authenticated?.Invoke(session);
        }
        catch (Exception ex)
        {
            ErrorMessage = ApiError.Describe(ex, "Sign-in failed.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ShowEnroll()
    {
        ErrorMessage = null;
        EnrollRequested?.Invoke();
    }

    /// <summary>
    /// The session survives a save failure: the user is signed in for this run either way, and
    /// §3.1 only promises the app stays logged in — it cannot promise a writable disk.
    /// </summary>
    private void Persist(AuthSession session)
    {
        try
        {
            _session.SaveFor(
                SessionStore.ToAgentRole(session.User.Role),
                new LocalAgentState
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Signed in but local session state could not be saved", ex);
            ErrorMessage = "Signed in, but this machine could not remember the session.";
        }
    }

    partial void OnDeviceKeyChanged(string value) => OnPropertyChanged(nameof(HasDeviceKey));

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(ShowError));
}
