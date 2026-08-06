using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Teamscop.App.Services;
using CommunityToolkit.Mvvm.Input;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly LocalAgentStore _store;
    private string _apiBaseUrl;
    private bool _suppressTzSideEffects;
    private List<AuthorityPackageInfo> _packageCatalog = [];
    private List<StaffCardViewModel> _allStaff = [];

    public SettingsViewModel(string? apiBaseUrl = null)
    {
        _store = AppSessionStore.ForActiveSession();
        _apiBaseUrl = ResolveApiBase(apiBaseUrl);
        foreach (var tz in BuildTimezoneOptions())
        {
            TimeZoneOptions.Add(tz);
        }

        SelectedTimeZoneId = "UTC";
        SeedLocalNow();
    }

    /// <summary>title, candidates → selected user id or null.</summary>
    public Func<string, IReadOnlyList<StaffCardViewModel>, Task<Guid?>>? RequestPickStaff { get; set; }

    public ObservableCollection<string> TimeZoneOptions { get; } = [];
    public ObservableCollection<PolicemanRowViewModel> Policemen { get; } = [];

    [ObservableProperty] private string _selectedTimeZoneId = "UTC";
    [ObservableProperty] private string _yearText = "2026";
    [ObservableProperty] private string _monthText = "1";
    [ObservableProperty] private string _dayText = "1";
    [ObservableProperty] private string _hourText = "0";
    [ObservableProperty] private string _minuteText = "0";
    [ObservableProperty] private string _secondText = "0";

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _policeStatusMessage;
    [ObservableProperty] private string? _policeErrorMessage;

    [ObservableProperty] private bool _isSynchronized;
    [ObservableProperty] private long _clockVersion;
    [ObservableProperty] private string _currentBusinessLocal = "—";
    [ObservableProperty] private string _currentTimeZoneLabel = "UTC";
    [ObservableProperty] private string _updatedAtLabel = "—";

    public bool CanSave => !IsBusy;
    public bool ShowStatus => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool ShowError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool ShowPoliceStatus => !string.IsNullOrWhiteSpace(PoliceStatusMessage);
    public bool ShowPoliceError => !string.IsNullOrWhiteSpace(PoliceErrorMessage);
    public bool HasPolicemen => Policemen.Count > 0;
    public bool ShowPoliceEmpty => !IsBusy && !HasPolicemen;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ShowPoliceEmpty));
    }

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(ShowStatus));
    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(ShowError));
    partial void OnPoliceStatusMessageChanged(string? value) => OnPropertyChanged(nameof(ShowPoliceStatus));
    partial void OnPoliceErrorMessageChanged(string? value) => OnPropertyChanged(nameof(ShowPoliceError));

    partial void OnSelectedTimeZoneIdChanged(string value)
    {
        if (_suppressTzSideEffects || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        // When admin picks a zone, refresh the editable wall-clock suggestion in that zone.
        ApplyWallClockSuggestion(value);
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
        PoliceErrorMessage = null;
        PoliceStatusMessage = null;
        try
        {
            using var api = new BusinessTimeApiClient(_apiBaseUrl);
            var cfg = await api.GetMineAsync(state.AccessToken).ConfigureAwait(false);
            BusinessTimeNowDto? now = null;
            try
            {
                now = await api.GetNowAsync(state.AccessToken).ConfigureAwait(false);
            }
            catch
            {
                // optional
            }

            await Dispatcher.UIThread.InvokeAsync(() => ApplyConfig(cfg, now));
            await LoadPoliceAsync(state.AccessToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ErrorMessage = FormatError(ex);
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    public void ApplyPolicemenRealtime(IReadOnlyList<PolicemanDto> list)
    {
        if (IsBusy)
        {
            return;
        }

        ReplacePolicemen(list);
        PoliceStatusMessage = "Policemen list synchronized.";
        PoliceErrorMessage = null;
        OnPropertyChanged(nameof(HasPolicemen));
        OnPropertyChanged(nameof(ShowPoliceEmpty));
    }

    public void ApplyBusinessTimeRealtime(BusinessClockConfig cfg)
    {
        ApplyConfig(cfg, now: null);
        StatusMessage = $"Business clock synchronized from server (v{cfg.ClockVersion}).";
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task SyncBusinessClockAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var state = _store.Load();
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            ErrorMessage = "Sign in required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedTimeZoneId))
        {
            ErrorMessage = "Time zone is required.";
            return;
        }

        if (!TryReadDateTime(out var year, out var month, out var day, out var hour, out var minute, out var second))
        {
            ErrorMessage = "Invalid business date/time.";
            return;
        }

        // Validate zone the same way the engine does.
        _ = BusinessClock.ResolveTimeZone(SelectedTimeZoneId);

        _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            using var api = new BusinessTimeApiClient(_apiBaseUrl);
            var cfg = await api.DeclareAsync(state.AccessToken, new DeclareBusinessTimeBody
            {
                TimeZoneId = SelectedTimeZoneId.Trim(),
                Year = year,
                Month = month,
                Day = day,
                Hour = hour,
                Minute = minute,
                Second = second
            }).ConfigureAwait(false);

            BusinessTimeNowDto? now = null;
            try
            {
                now = await api.GetNowAsync(state.AccessToken).ConfigureAwait(false);
            }
            catch
            {
                // optional
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyConfig(cfg, now);
                StatusMessage = $"Business clock synchronized (v{cfg.ClockVersion}).";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ErrorMessage = FormatError(ex);
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    [RelayCommand]
    private void UseSuggestedNow()
    {
        ApplyWallClockSuggestion(SelectedTimeZoneId);
        StatusMessage = "Filled with current time in the selected zone.";
    }

    [RelayCommand]
    private async Task AddPolicemanAsync()
    {
        if (IsBusy || RequestPickStaff is null)
        {
            return;
        }

        var state = _store.Load();
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            PoliceErrorMessage = "Sign in required.";
            return;
        }

        // Stay on UI thread until after the picker — Window creation requires it.
        await EnsureStaffPoolAsync(state.AccessToken);
        var policeIds = Policemen.Select(p => p.StaffUserId).ToHashSet();
        var candidates = _allStaff.Where(s => !policeIds.Contains(s.UserId)).ToList();
        if (candidates.Count == 0)
        {
            PoliceErrorMessage = "No staff left to promote.";
            return;
        }

        var picked = await RequestPickStaff.Invoke("Make policeman", candidates);
        if (picked is null)
        {
            return;
        }

        _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
        IsBusy = true;
        PoliceErrorMessage = null;
        PoliceStatusMessage = null;
        try
        {
            using var police = new PoliceApiClient(_apiBaseUrl);
            await police.UpsertPolicemanAsync(state.AccessToken, picked.Value, []).ConfigureAwait(false);
            var list = await police.ListPolicemenAsync(state.AccessToken).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ReplacePolicemen(list);
                PoliceStatusMessage = "Policeman added — grant packages and save. Staff agents sync immediately.";
                OnPropertyChanged(nameof(HasPolicemen));
                OnPropertyChanged(nameof(ShowPoliceEmpty));
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => PoliceErrorMessage = FormatError(ex));
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    [RelayCommand]
    private async Task SavePolicemanAsync(PolicemanRowViewModel? row)
    {
        if (row is null || IsBusy)
        {
            return;
        }

        var state = _store.Load();
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            PoliceErrorMessage = "Sign in required.";
            return;
        }

        _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
        IsBusy = true;
        PoliceErrorMessage = null;
        PoliceStatusMessage = null;
        try
        {
            using var police = new PoliceApiClient(_apiBaseUrl);
            await police.UpsertPolicemanAsync(
                state.AccessToken, row.StaffUserId, row.SelectedPackageIds()).ConfigureAwait(false);
            var list = await police.ListPolicemenAsync(state.AccessToken).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ReplacePolicemen(list);
                PoliceStatusMessage = $"{row.Username} authorities saved — synced to their agent.";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => PoliceErrorMessage = FormatError(ex));
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    [RelayCommand]
    private async Task RevokePolicemanAsync(PolicemanRowViewModel? row)
    {
        if (row is null || IsBusy)
        {
            return;
        }

        var state = _store.Load();
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            PoliceErrorMessage = "Sign in required.";
            return;
        }

        _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
        IsBusy = true;
        PoliceErrorMessage = null;
        PoliceStatusMessage = null;
        try
        {
            using var police = new PoliceApiClient(_apiBaseUrl);
            await police.RevokePolicemanAsync(state.AccessToken, row.StaffUserId).ConfigureAwait(false);
            var list = await police.ListPolicemenAsync(state.AccessToken).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ReplacePolicemen(list);
                PoliceStatusMessage = $"{row.Username} policeman status revoked — synced immediately.";
                OnPropertyChanged(nameof(HasPolicemen));
                OnPropertyChanged(nameof(ShowPoliceEmpty));
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => PoliceErrorMessage = FormatError(ex));
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    private async Task LoadPoliceAsync(string accessToken)
    {
        using var police = new PoliceApiClient(_apiBaseUrl);
        var catalog = await police.ListPackagesAsync(accessToken).ConfigureAwait(false);
        var list = await police.ListPolicemenAsync(accessToken).ConfigureAwait(false);
        await EnsureStaffPoolAsync(accessToken).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _packageCatalog = catalog.ToList();
            ReplacePolicemen(list);
            OnPropertyChanged(nameof(HasPolicemen));
            OnPropertyChanged(nameof(ShowPoliceEmpty));
        });
    }

    private async Task EnsureStaffPoolAsync(string accessToken)
    {
        using var org = new OrgApiClient(_apiBaseUrl);
        var structure = await org.GetStructureAsync(accessToken).ConfigureAwait(false);
        var map = new Dictionary<Guid, StaffCardViewModel>();
        foreach (var s in structure.UnassignedStaff)
        {
            map[s.UserId] = StaffCardViewModel.FromDto(s);
        }

        foreach (var team in structure.Teams)
        {
            if (team.Leader is not null)
            {
                map[team.Leader.UserId] = StaffCardViewModel.FromDto(team.Leader, team.TeamId, isLeader: true);
            }

            foreach (var m in team.Members)
            {
                map[m.UserId] = StaffCardViewModel.FromDto(m, team.TeamId);
            }
        }

        _allStaff = map.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void ReplacePolicemen(IReadOnlyList<PolicemanDto> list)
    {
        var catalog = _packageCatalog.Count > 0
            ? _packageCatalog
            : AuthorityPackageIds.All
                .Select(id => new AuthorityPackageInfo
                {
                    Id = id,
                    Label = AuthorityPackageIds.Labels.TryGetValue(id, out var label) ? label : id
                })
                .ToList();

        Policemen.Clear();
        foreach (var p in list.OrderBy(x => x.Username, StringComparer.OrdinalIgnoreCase))
        {
            Policemen.Add(new PolicemanRowViewModel(
                p.StaffUserId,
                p.Username,
                catalog,
                p.Packages));
        }
    }

    private void ApplyConfig(BusinessClockConfig cfg, BusinessTimeNowDto? now)
    {
        _suppressTzSideEffects = true;
        try
        {
            EnsureTimezoneOption(cfg.TimeZoneId);
            SelectedTimeZoneId = string.IsNullOrWhiteSpace(cfg.TimeZoneId) ? "UTC" : cfg.TimeZoneId;
            IsSynchronized = cfg.IsSynchronized;
            ClockVersion = cfg.ClockVersion;
            CurrentTimeZoneLabel = SelectedTimeZoneId;
            var clock = new BusinessClock();
            clock.Apply(cfg);
            UpdatedAtLabel = BusinessTimeDisplay.FormatUtcInstant(cfg.UpdatedAt, clock)
                             + $" ({SelectedTimeZoneId})";

            if (now is not null && !string.IsNullOrWhiteSpace(now.BusinessLocal)
                && DateTime.TryParse(now.BusinessLocal, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out var biz))
            {
                CurrentBusinessLocal = now.BusinessLocal;
                SetDateParts(biz);
            }
            else if (cfg.IsSynchronized && cfg.AnchorBusinessLocal is { } anchor)
            {
                CurrentBusinessLocal = anchor.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
                SetDateParts(anchor);
            }
            else
            {
                CurrentBusinessLocal = "Not synchronized";
                ApplyWallClockSuggestion(SelectedTimeZoneId);
            }
        }
        finally
        {
            _suppressTzSideEffects = false;
        }
    }

    private void ApplyWallClockSuggestion(string timeZoneId)
    {
        var tz = BusinessClock.ResolveTimeZone(timeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        SetDateParts(local);
    }

    private void SetDateParts(DateTime local)
    {
        YearText = local.Year.ToString(CultureInfo.InvariantCulture);
        MonthText = local.Month.ToString(CultureInfo.InvariantCulture);
        DayText = local.Day.ToString(CultureInfo.InvariantCulture);
        HourText = local.Hour.ToString(CultureInfo.InvariantCulture);
        MinuteText = local.Minute.ToString(CultureInfo.InvariantCulture);
        SecondText = local.Second.ToString(CultureInfo.InvariantCulture);
    }

    private bool TryReadDateTime(
        out int year, out int month, out int day, out int hour, out int minute, out int second)
    {
        year = month = day = hour = minute = second = 0;
        if (!int.TryParse(YearText, NumberStyles.Integer, CultureInfo.InvariantCulture, out year)
            || !int.TryParse(MonthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out month)
            || !int.TryParse(DayText, NumberStyles.Integer, CultureInfo.InvariantCulture, out day)
            || !int.TryParse(HourText, NumberStyles.Integer, CultureInfo.InvariantCulture, out hour)
            || !int.TryParse(MinuteText, NumberStyles.Integer, CultureInfo.InvariantCulture, out minute)
            || !int.TryParse(SecondText, NumberStyles.Integer, CultureInfo.InvariantCulture, out second))
        {
            return false;
        }

        try
        {
            _ = new DateTime(year, month, day, hour, minute, second);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private void SeedLocalNow() => ApplyWallClockSuggestion("UTC");

    private void EnsureTimezoneOption(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        if (!TimeZoneOptions.Contains(id))
        {
            TimeZoneOptions.Insert(0, id);
        }
    }

    private static IEnumerable<string> BuildTimezoneOptions()
    {
        var list = new List<string>
        {
            "UTC",
            "UTC+00:00",
            "UTC+01:00",
            "UTC+02:00",
            "UTC+03:00",
            "UTC+03:30",
            "UTC+04:00",
            "UTC+05:00",
            "UTC+05:30",
            "UTC+05:45",
            "UTC+06:00",
            "UTC+07:00",
            "UTC+08:00",
            "UTC+09:00",
            "UTC+09:30",
            "UTC+10:00",
            "UTC+11:00",
            "UTC+12:00",
            "UTC-01:00",
            "UTC-02:00",
            "UTC-03:00",
            "UTC-04:00",
            "UTC-05:00",
            "UTC-06:00",
            "UTC-07:00",
            "UTC-08:00",
            "UTC-09:00",
            "UTC-10:00",
            "UTC-11:00",
            "UTC-12:00"
        };

        try
        {
            foreach (var tz in TimeZoneInfo.GetSystemTimeZones().OrderBy(t => t.Id))
            {
                if (!list.Contains(tz.Id, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(tz.Id);
                }
            }
        }
        catch
        {
            // offsets-only is enough for PHASE5 fixed-offset design
        }

        return list;
    }

    private static string FormatError(Exception ex)
    {
        while (ex is AggregateException { InnerException: { } inner })
        {
            ex = inner;
        }

        if (ex is ApiClientException api)
        {
            foreach (var prefix in new[] { "BusinessTime API: ", "Police API: ", "Org API: " })
            {
                if (api.Message.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return api.Message[prefix.Length..];
                }
            }

            return api.Message;
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
