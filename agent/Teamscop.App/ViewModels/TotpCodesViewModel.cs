using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Teamscop.App.Composition;
using Teamscop.App.Services;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.ViewModels;

public sealed partial class TotpStaffRowViewModel : ObservableObject
{
    public Guid StaffUserId { get; init; }
    public string Username { get; init; } = "";

    [ObservableProperty] private bool _isSelected;

    public string DisplayName => Username;
}

/// <summary>
/// §1.4 / §1.5 — the current 6-digit approval code for a chosen staff member and purpose. §6 makes
/// the credential deterministic from the machine key, so there is no secret, no storage and no
/// enrolment step: a code is always available for any staff member and the screen simply shows it.
///
/// It is the mechanism by which a code-issuer (admin, or a holder of usb_approval / uninstall_approval)
/// reads a code to relay out of band (§10.1–10.2). The list is staff only — the admin's own machine
/// is never offered here (§1.5).
/// </summary>
public sealed partial class TotpCodesViewModel : ObservableObject, IDisposable
{
    private readonly TeamscopApi _api;
    private readonly SessionStore _session;
    private readonly UiLog _log;

    /// <summary>The code is a TOTP: its window really is closing, so the counter really ticks.</summary>
    private readonly DispatcherTimer _expiry;
    private bool _isActive;

    public TotpCodesViewModel(AppServices services)
    {
        _api = services.Api;
        _session = services.Session;
        _log = services.Log;
        _expiry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _expiry.Tick += OnExpiryTick;
    }

    public ObservableCollection<TotpStaffRowViewModel> StaffRows { get; } = [];

    [ObservableProperty] private TotpStaffRowViewModel? _selectedStaff;
    [ObservableProperty] private string? _code;
    [ObservableProperty] private int _remainingSeconds;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string? _errorMessage;

    /// <summary>usb | uninstall — purpose-scoped code (F-H4). Both are available for any staff member.</summary>
    [ObservableProperty] private string _selectedPurpose = TotpGenerator.PurposeUsb;

    /// <summary>The USB unlock code. Shown next to the uninstall one, never instead of it.</summary>
    [ObservableProperty] private string? _usbCode;

    /// <summary>
    /// The uninstall code. A different derivation from the USB one, so handing over the wrong
    /// one is refused by the guard on every attempt — which is why both are on screen together.
    /// </summary>
    [ObservableProperty] private string? _uninstallCode;

    public bool HasCode => !string.IsNullOrWhiteSpace(Code);
    public bool ShowStatus => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool ShowError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasStaff => StaffRows.Count > 0;
    public bool IsUsbPurpose => string.Equals(SelectedPurpose, TotpGenerator.PurposeUsb, StringComparison.OrdinalIgnoreCase);
    public bool IsUninstallPurpose => !IsUsbPurpose;

    public string EmptyListMessage =>
        "No staff listed yet. A staff member appears here as soon as they register.";

    /// <summary>Leaving the route stops the countdown; nothing ticks against a screen nobody sees.</summary>
    public void SetActive(bool active)
    {
        _isActive = active;
        ArmExpiry();
    }

    partial void OnCodeChanged(string? value)
    {
        OnPropertyChanged(nameof(HasCode));
        ArmExpiry();
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(HasStaff));

    partial void OnSelectedPurposeChanged(string value)
    {
        OnPropertyChanged(nameof(IsUsbPurpose));
        OnPropertyChanged(nameof(IsUninstallPurpose));
        if (SelectedStaff is not null)
        {
            RefreshCodeAsync().FireAndForget(_log, "Access code");
        }
    }

    /// <summary>The countdown runs exactly while a live code is on a screen someone is looking at.</summary>
    private void ArmExpiry()
    {
        if (_isActive && HasCode && RemainingSeconds > 0)
        {
            _expiry.Start();
        }
        else
        {
            _expiry.Stop();
        }
    }

    /// <summary>
    /// The server returns the seconds left in the current window; without this the number the
    /// issuer reads out sat frozen while the code it names quietly expired.
    /// </summary>
    private void OnExpiryTick(object? sender, EventArgs e)
    {
        if (RemainingSeconds > 1)
        {
            RemainingSeconds--;
            return;
        }

        _expiry.Stop();
        RemainingSeconds = 0;
        if (SelectedStaff is not null && _isActive)
        {
            // The window has rolled over, so fetch the code that is valid now.
            RefreshCodeAsync().FireAndForget(_log, "Access code");
        }
        else
        {
            Code = null;
        }
    }

    public void Dispose()
    {
        _expiry.Stop();
        _expiry.Tick -= OnExpiryTick;
    }

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
        ErrorMessage = null;

        if (value is not null)
        {
            RefreshCodeAsync().FireAndForget(_log, "Access code");
        }
    }

    [RelayCommand]
    private void SelectPurpose(string purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose))
        {
            return;
        }

        SelectedPurpose = purpose.Trim().ToLowerInvariant();
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (!_session.HasToken)
        {
            ErrorMessage = "Sign in required.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var list = await _api.ListStaffTotpAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // A code from a previous visit is stale by now; clearing the selection drops it
                // and its counter rather than leaving a dead six digits on screen.
                SelectedStaff = null;
                StaffRows.Clear();
                foreach (var s in list.OrderBy(x => x.StaffUsername, StringComparer.OrdinalIgnoreCase))
                {
                    StaffRows.Add(new TotpStaffRowViewModel
                    {
                        StaffUserId = s.StaffUserId,
                        Username = s.StaffUsername
                    });
                }

                OnPropertyChanged(nameof(HasStaff));
                StatusMessage = HasStaff
                    ? "Select a staff member to show the current approval code."
                    : null;
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The route was left; the next entry reloads.
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = ApiError.Describe(ex, "Request failed."));
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    [RelayCommand]
    private async Task RefreshCodeAsync()
    {
        if (SelectedStaff is null || IsBusy)
        {
            return;
        }

        if (!_session.HasToken)
        {
            ErrorMessage = "Sign in required.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            // Both codes, always, side by side.
            //
            // They used to be behind a USB/Uninstall toggle that defaulted to USB, so an admin who
            // did not notice it read out a USB code for an uninstall. The two are derived from
            // different purpose strings, so the guard refuses the wrong one every single time — and
            // the failure looked like "uninstall is broken for every employee" rather than like a
            // mispicked toggle. Showing both removes the mode that could be got wrong.
            // Fetched independently: the server now refuses a purpose the caller does not hold
            // (rather than silently substituting the other one), so for a single-package policeman
            // one of these two legitimately fails and the other must still display.
            var staffId = SelectedStaff.StaffUserId;
            var usb = await TryFetchAsync(staffId, TotpGenerator.PurposeUsb).ConfigureAwait(false);
            var uninstall = await TryFetchAsync(staffId, TotpGenerator.PurposeUninstall).ConfigureAwait(false);
            if (usb is null && uninstall is null)
            {
                throw new InvalidOperationException("No code purpose is available to this account.");
            }

            var primary = usb ?? uninstall!;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RemainingSeconds = primary.RemainingSeconds;
                UsbCode = usb?.Code;
                UninstallCode = uninstall?.Code;
                // Kept so existing bindings and the expiry countdown keep working.
                Code = primary.Code;
                // Explicit: two consecutive windows can, very rarely, produce the same six digits,
                // and then setting Code raises nothing.
                ArmExpiry();
                StatusMessage = $"Codes for {primary.StaffUsername} — relay the matching one to the staff member.";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Code = null;
                ErrorMessage = ApiError.Describe(ex, "Request failed.");
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    [RelayCommand]
    private Task CopyCodeAsync() => CopyAsync(ClipboardText(), "Codes copied.", "Could not copy codes.");

    [RelayCommand]
    private Task CopyUsbCodeAsync() => CopyAsync(UsbCode, "USB code copied.", "Could not copy code.");

    [RelayCommand]
    private Task CopyUninstallCodeAsync() => CopyAsync(UninstallCode, "Uninstall code copied.", "Could not copy code.");

    /// <summary>Both codes, labelled — copying only one invited relaying it for the wrong purpose.</summary>
    private string? ClipboardText()
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(UsbCode))
        {
            parts.Add($"USB: {UsbCode}");
        }

        if (!string.IsNullOrWhiteSpace(UninstallCode))
        {
            parts.Add($"Uninstall: {UninstallCode}");
        }

        return parts.Count == 0 ? null : string.Join("  ", parts);
    }

    /// <summary>One purpose's code, or null when this account may not issue that purpose.</summary>
    private async Task<TotpCodeResult?> TryFetchAsync(Guid staffUserId, string purpose)
    {
        try
        {
            return await _api.GetTotpCodeAsync(staffUserId, purpose, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task CopyAsync(string? text, string ok, string failure)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            // Not MainWindow: a monitored machine deliberately leaves it unassigned so the shell
            // does not flash on boot, and this screen is exactly what a policeman on such a
            // machine uses. Take the clipboard from whichever window is actually up.
            var clipboard = Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.Windows.FirstOrDefault(w => w.IsActive)?.Clipboard
                  ?? desktop.Windows.Select(w => w.Clipboard).FirstOrDefault(c => c is not null)
                : null;

            if (clipboard is null)
            {
                ErrorMessage = failure;
                return;
            }

            await clipboard.SetTextAsync(text);
            StatusMessage = ok;
        }
        catch (Exception ex)
        {
            _log.Warn("Clipboard copy failed", ex);
            ErrorMessage = failure;
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
}
