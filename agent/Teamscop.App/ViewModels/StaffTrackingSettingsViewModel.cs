using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Teamscop.App.Composition;
using Teamscop.App.Services;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.ViewModels;

public sealed record QualityOption(ScreenshotQuality Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record PeriodOption(int Seconds, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class StaffTrackingSettingsViewModel : ObservableObject
{
    private readonly TeamscopApi _api;
    private readonly SessionStore _session;
    private readonly CompanyClock _clock;
    private Guid? _staffUserId;
    private Guid? _loadedForStaff;
    private int _loadGeneration;
    private StaffTrackingConfig? _loaded;

    public StaffTrackingSettingsViewModel(AppServices services)
    {
        _api = services.Api;
        _session = services.Session;
        _clock = services.Clock;
        SelectedQuality = QualityOptions.First(q => q.Value == ScreenshotQuality.Medium);
        SelectedPeriod = PeriodOptions.First(p => p.Seconds == 180);
    }

    public IReadOnlyList<QualityOption> QualityOptions { get; } =
    [
        new(ScreenshotQuality.Low, "Low (≤30 KB)"),
        new(ScreenshotQuality.Medium, "Medium (≤50 KB)"),
        new(ScreenshotQuality.High, "High (≤70 KB)")
    ];

    public IReadOnlyList<PeriodOption> PeriodOptions { get; } =
    [
        new(60, "1 minute"),
        new(120, "2 minutes"),
        new(180, "3 minutes"),
        new(300, "5 minutes"),
        new(600, "10 minutes"),
        new(900, "15 minutes"),
        new(1800, "30 minutes"),
        new(3600, "60 minutes")
    ];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string _staffName = "";
    [ObservableProperty] private QualityOption? _selectedQuality;
    [ObservableProperty] private PeriodOption? _selectedPeriod;
    [ObservableProperty] private string _configVersionLabel = "";
    [ObservableProperty] private bool _hasLoaded;

    public bool CanEdit => HasLoaded && !IsLoading && !IsSaving;
    public bool ShowError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool ShowStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(CanEdit));
    partial void OnIsSavingChanged(bool value) => OnPropertyChanged(nameof(CanEdit));
    partial void OnHasLoadedChanged(bool value) => OnPropertyChanged(nameof(CanEdit));
    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(ShowError));
    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(ShowStatus));

    public void Reset()
    {
        Interlocked.Increment(ref _loadGeneration);
        _staffUserId = null;
        _loadedForStaff = null;
        _loaded = null;
        StaffName = "";
        HasLoaded = false;
        ErrorMessage = null;
        StatusMessage = null;
        ConfigVersionLabel = "";
        SelectedQuality = QualityOptions.First(q => q.Value == ScreenshotQuality.Medium);
        SelectedPeriod = PeriodOptions.First(p => p.Seconds == 180);
    }

    public async Task LoadAsync(
        Guid staffUserId, string staffName, bool force = false, CancellationToken ct = default)
    {
        if (!force && _loadedForStaff == staffUserId && HasLoaded)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _loadGeneration);
        _staffUserId = staffUserId;
        StaffName = staffName;

        if (!_session.HasToken)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                HasLoaded = false;
                ErrorMessage = "Sign in required.";
                StatusMessage = null;
                IsLoading = false;
            });
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsLoading = true;
            ErrorMessage = null;
            StatusMessage = null;
        });

        try
        {
            var cfg = await _api.GetStaffTrackingConfigAsync(staffUserId, ct)
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                ApplyLoaded(cfg);
                _loadedForStaff = staffUserId;
                IsLoading = false;
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled by a newer selection or by leaving the route. Hand the section back rather
            // than leaving the spinner up: a newer load owns the flag once the generation moves.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                IsLoading = false;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                HasLoaded = false;
                ErrorMessage = ApiError.Describe(ex, "Failed to load or save settings.");
                IsLoading = false;
            });
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_staffUserId is not { } staffUserId || SelectedQuality is null || SelectedPeriod is null)
        {
            return;
        }

        if (!_session.HasToken)
        {
            ErrorMessage = "Sign in required.";
            return;
        }

        // The same guard the load path uses: switching staff mid-save must not let this response
        // repaint the next member's settings with the previous member's values.
        var generation = _loadGeneration;
        IsSaving = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            var body = _loaded ?? new StaffTrackingConfig { StaffUserId = staffUserId };
            body.StaffUserId = staffUserId;
            body.ScreenshotQuality = SelectedQuality.Value;
            body.ScreenshotPeriodSeconds = SelectedPeriod.Seconds;

            var updated = await _api.UpsertStaffTrackingConfigAsync(
                    staffUserId, body, CancellationToken.None)
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                ApplyLoaded(updated);
                StatusMessage = "Saved. Staff agent updates immediately when online (SignalR).";
                IsSaving = false;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                ErrorMessage = ApiError.Describe(ex, "Failed to load or save settings.");
                IsSaving = false;
            });
        }
    }

    private void ApplyLoaded(StaffTrackingConfig cfg)
    {
        _loaded = cfg;
        SelectedQuality = QualityOptions.FirstOrDefault(q => q.Value == cfg.ScreenshotQuality)
                          ?? QualityOptions.First(q => q.Value == ScreenshotQuality.Medium);
        SelectedPeriod = PeriodOptions.FirstOrDefault(p => p.Seconds == cfg.ScreenshotPeriodSeconds)
                         ?? ClosestPeriod(cfg.ScreenshotPeriodSeconds);
        // Company time, like every other timestamp in the app (§8.2) — not the viewer's zone.
        ConfigVersionLabel =
            $"Config version {cfg.ConfigVersion} · updated {_clock.FormatDateTime(cfg.UpdatedAt)}";
        HasLoaded = true;
        ErrorMessage = null;
    }

    private PeriodOption ClosestPeriod(int seconds)
    {
        return PeriodOptions
            .OrderBy(p => Math.Abs(p.Seconds - Math.Max(30, seconds)))
            .First();
    }

}
