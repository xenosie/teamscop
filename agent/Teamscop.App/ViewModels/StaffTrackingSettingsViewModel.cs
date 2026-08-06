using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Teamscop.App.Services;
using CommunityToolkit.Mvvm.Input;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;
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
    private readonly LocalAgentStore _store;
    private string _apiBaseUrl;
    private Guid? _staffUserId;
    private Guid? _loadedForStaff;
    private int _loadGeneration;
    private StaffTrackingConfig? _loaded;

    public StaffTrackingSettingsViewModel(string? apiBaseUrl = null)
    {
        _store = AppSessionStore.ForActiveSession();
        _apiBaseUrl = ResolveApiBase(apiBaseUrl);
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

    public async Task LoadAsync(Guid staffUserId, string staffName, bool force = false)
    {
        if (!force && _loadedForStaff == staffUserId && HasLoaded)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _loadGeneration);
        _staffUserId = staffUserId;
        StaffName = staffName;

        var state = _store.Load();
        if (string.IsNullOrWhiteSpace(state.AccessToken))
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

        _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsLoading = true;
            ErrorMessage = null;
            StatusMessage = null;
        });

        try
        {
            using var api = new TrackingApiClient(_apiBaseUrl);
            var cfg = await api.GetStaffTrackingConfigAsync(state.AccessToken, staffUserId)
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                ApplyLoaded(cfg);
                _loadedForStaff = staffUserId;
                IsLoading = false;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                HasLoaded = false;
                ErrorMessage = FormatError(ex);
                IsLoading = false;
            });
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_staffUserId is null || SelectedQuality is null || SelectedPeriod is null)
        {
            return;
        }

        var state = _store.Load();
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            ErrorMessage = "Sign in required.";
            return;
        }

        IsSaving = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
            var body = _loaded ?? new StaffTrackingConfig { StaffUserId = _staffUserId.Value };
            body.StaffUserId = _staffUserId.Value;
            body.ScreenshotQuality = SelectedQuality.Value;
            body.ScreenshotPeriodSeconds = SelectedPeriod.Seconds;

            using var api = new TrackingApiClient(_apiBaseUrl);
            var updated = await api.UpsertStaffTrackingConfigAsync(
                    state.AccessToken, _staffUserId.Value, body)
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyLoaded(updated);
                StatusMessage = "Saved. Staff agent updates immediately when online (SignalR).";
                IsSaving = false;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ErrorMessage = FormatError(ex);
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
        ConfigVersionLabel = $"Config version {cfg.ConfigVersion} · updated {cfg.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm}";
        HasLoaded = true;
        ErrorMessage = null;
    }

    private PeriodOption ClosestPeriod(int seconds)
    {
        return PeriodOptions
            .OrderBy(p => Math.Abs(p.Seconds - Math.Max(30, seconds)))
            .First();
    }

    private static string FormatError(Exception ex)
    {
        if (ex is HttpRequestException)
        {
            return "Could not reach the server.";
        }

        return string.IsNullOrWhiteSpace(ex.Message) ? "Failed to load or save settings." : ex.Message;
    }

    private static string ResolveApiBase(string? apiBaseUrl)
        => string.IsNullOrWhiteSpace(apiBaseUrl)
            ? Environment.GetEnvironmentVariable("TEAMSCOP_API_BASE") ?? "https://teamscop.com"
            : apiBaseUrl.TrimEnd('/');
}
