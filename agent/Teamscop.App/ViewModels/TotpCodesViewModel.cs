using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Teamscop.App.Services;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.ViewModels;

public sealed partial class TotpStaffRowViewModel : ObservableObject
{
    public Guid StaffUserId { get; init; }
    public string Username { get; init; } = "";
    public bool Enabled { get; init; }

    [ObservableProperty] private bool _isSelected;

    public string StatusLabel => Enabled ? "Enrolled" : "Not enrolled";
}

/// <summary>
/// Generate current USB / uninstall TOTP codes for staff.
/// Visible to policemen with usb_approval and/or uninstall_approval packages.
/// </summary>
public sealed partial class TotpCodesViewModel : ObservableObject
{
    private readonly LocalAgentStore _store;
    private string _apiBaseUrl;

    public TotpCodesViewModel(string? apiBaseUrl = null)
    {
        _store = AppSessionStore.ForActiveSession();
        _apiBaseUrl = ResolveApiBase(apiBaseUrl);
    }

    public ObservableCollection<TotpStaffRowViewModel> StaffRows { get; } = [];

    [ObservableProperty] private TotpStaffRowViewModel? _selectedStaff;
    [ObservableProperty] private string? _code;
    [ObservableProperty] private int _remainingSeconds;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string? _errorMessage;

    public bool HasCode => !string.IsNullOrWhiteSpace(Code);
    public bool ShowStatus => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool ShowError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasStaff => StaffRows.Count > 0;

    partial void OnCodeChanged(string? value) => OnPropertyChanged(nameof(HasCode));
    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(ShowStatus));
    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(ShowError));

    partial void OnSelectedStaffChanged(TotpStaffRowViewModel? value)
    {
        foreach (var row in StaffRows)
        {
            row.IsSelected = value is not null && row.StaffUserId == value.StaffUserId;
        }

        Code = null;
        RemainingSeconds = 0;
        if (value is { Enabled: true })
        {
            _ = RefreshCodeAsync();
        }
        else if (value is not null)
        {
            ErrorMessage = "This staff has no TOTP enrolled yet. Ask an admin to enroll them.";
        }
    }

    public async Task LoadAsync()
    {
        var state = _store.Load();
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            ErrorMessage = "Sign in required.";
            return;
        }

        _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            using var api = new LifecycleApiClient(_apiBaseUrl);
            var list = await api.ListStaffTotpAsync(state.AccessToken).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StaffRows.Clear();
                foreach (var s in list.OrderBy(x => x.StaffUsername, StringComparer.OrdinalIgnoreCase))
                {
                    StaffRows.Add(new TotpStaffRowViewModel
                    {
                        StaffUserId = s.StaffUserId,
                        Username = s.StaffUsername,
                        Enabled = s.Enabled
                    });
                }

                OnPropertyChanged(nameof(HasStaff));
                StatusMessage = "Select enrolled staff to show the current approval code.";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = FormatError(ex));
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    [RelayCommand]
    private async Task RefreshCodeAsync()
    {
        if (SelectedStaff is null || !SelectedStaff.Enabled || IsBusy)
        {
            return;
        }

        var state = _store.Load();
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            ErrorMessage = "Sign in required.";
            return;
        }

        _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            using var api = new LifecycleApiClient(_apiBaseUrl);
            var result = await api.GetTotpCodeAsync(state.AccessToken, SelectedStaff.StaffUserId)
                .ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Code = result.Code;
                RemainingSeconds = result.RemainingSeconds;
                StatusMessage = $"Code for {result.StaffUsername} — give this to the staff member.";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Code = null;
                ErrorMessage = FormatError(ex);
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    [RelayCommand]
    private async Task CopyCodeAsync()
    {
        if (string.IsNullOrWhiteSpace(Code))
        {
            return;
        }

        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow: { Clipboard: { } clipboard }
                })
            {
                await clipboard.SetTextAsync(Code);
                StatusMessage = "Code copied.";
            }
        }
        catch
        {
            ErrorMessage = "Could not copy code.";
        }
    }

    [RelayCommand]
    private void SelectStaff(TotpStaffRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        SelectedStaff = row;
    }

    private static string FormatError(Exception ex)
    {
        while (ex is AggregateException { InnerException: { } inner })
        {
            ex = inner;
        }

        if (ex is ApiClientException api)
        {
            const string prefix = "Lifecycle API: ";
            return api.Message.StartsWith(prefix, StringComparison.Ordinal)
                ? api.Message[prefix.Length..]
                : api.Message;
        }

        return ex is HttpRequestException
            ? "Could not reach the server."
            : (string.IsNullOrWhiteSpace(ex.Message) ? "Request failed." : ex.Message);
    }

    private static string ResolveApiBase(string? apiBaseUrl)
        => string.IsNullOrWhiteSpace(apiBaseUrl)
            ? Environment.GetEnvironmentVariable("TEAMSCOP_API_BASE") ?? "https://teamscop.com"
            : apiBaseUrl.TrimEnd('/');
}
