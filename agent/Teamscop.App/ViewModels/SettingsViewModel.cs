using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Teamscop.App.Composition;
using Teamscop.App.Services;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly TeamscopApi _api;
    private readonly SessionStore _session;
    private readonly CompanyClock _clock;
    private List<AuthorityPackageInfo> _packageCatalog = [];
    private List<StaffCardViewModel> _allStaff = [];

    public SettingsViewModel(AppServices services)
    {
        _api = services.Api;
        _session = services.Session;
        _clock = services.Clock;
        foreach (var tz in TimeZoneCatalog.Build())
        {
            TimeZoneOptions.Add(tz);
        }

        SelectedTimeZone = FindOrAddTimeZone("UTC");
    }

    /// <summary>title, candidates → selected user id or null.</summary>
    public Func<string, IReadOnlyList<StaffCardViewModel>, Task<Guid?>>? RequestPickStaff { get; set; }

    public ObservableCollection<TimeZoneOption> TimeZoneOptions { get; } = [];
    public ObservableCollection<PolicemanRowViewModel> Policemen { get; } = [];

    [ObservableProperty] private TimeZoneOption? _selectedTimeZone;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _policeStatusMessage;
    [ObservableProperty] private string? _policeErrorMessage;

    [ObservableProperty] private string _currentBusinessLocal = "—";
    [ObservableProperty] private string _currentTimeZoneLabel = "UTC";

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
        PoliceErrorMessage = null;
        PoliceStatusMessage = null;
        try
        {
            var cfg = await _api.GetBusinessTimeAsync(ct).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => ApplyConfig(cfg));
            await LoadPoliceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The route was left; the next entry reloads.
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ErrorMessage = ApiError.Describe(ex, "Request failed.");
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
        ApplyConfig(cfg);
        StatusMessage = $"Company time zone updated to {CurrentTimeZoneLabel}.";
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task SaveTimeZoneAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (!_session.HasToken)
        {
            ErrorMessage = "Sign in required.";
            return;
        }

        var zoneId = SelectedTimeZone?.Id.Trim();
        if (string.IsNullOrWhiteSpace(zoneId))
        {
            ErrorMessage = "Pick a time zone.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            // PUT /api/business-time { timeZoneId } — the zone is the whole setting (§8.4).
            var zone = await _api.SetBusinessTimeZoneAsync(zoneId, CancellationToken.None)
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyConfig(new BusinessClockConfig
                {
                    CompanyId = zone.CompanyId,
                    TimeZoneId = zone.TimeZoneId,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
                StatusMessage = $"Company time zone set to {CurrentTimeZoneLabel} — staff agents sync immediately.";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ErrorMessage = ApiError.Describe(ex, "Request failed.");
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    [RelayCommand]
    private async Task AddPolicemanAsync()
    {
        if (IsBusy || RequestPickStaff is null)
        {
            return;
        }

        if (!_session.HasToken)
        {
            PoliceErrorMessage = "Sign in required.";
            return;
        }

        // Stay on UI thread until after the picker — Window creation requires it.
        await EnsureStaffPoolAsync(CancellationToken.None);
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

        IsBusy = true;
        PoliceErrorMessage = null;
        PoliceStatusMessage = null;
        try
        {
            await _api.UpsertPolicemanAsync(picked.Value, [], CancellationToken.None).ConfigureAwait(false);
            var list = await _api.ListPolicemenAsync(CancellationToken.None).ConfigureAwait(false);
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
            await Dispatcher.UIThread.InvokeAsync(
                () => PoliceErrorMessage = ApiError.Describe(ex, "Request failed."));
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

        if (!_session.HasToken)
        {
            PoliceErrorMessage = "Sign in required.";
            return;
        }

        IsBusy = true;
        PoliceErrorMessage = null;
        PoliceStatusMessage = null;
        try
        {
            await _api.UpsertPolicemanAsync(
                row.StaffUserId, row.SelectedPackageIds(), CancellationToken.None).ConfigureAwait(false);
            var list = await _api.ListPolicemenAsync(CancellationToken.None).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ReplacePolicemen(list);
                PoliceStatusMessage = $"{row.Username} authorities saved — synced to their agent.";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(
                () => PoliceErrorMessage = ApiError.Describe(ex, "Request failed."));
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

        if (!_session.HasToken)
        {
            PoliceErrorMessage = "Sign in required.";
            return;
        }

        IsBusy = true;
        PoliceErrorMessage = null;
        PoliceStatusMessage = null;
        try
        {
            await _api.RevokePolicemanAsync(row.StaffUserId, CancellationToken.None).ConfigureAwait(false);
            var list = await _api.ListPolicemenAsync(CancellationToken.None).ConfigureAwait(false);
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
            await Dispatcher.UIThread.InvokeAsync(
                () => PoliceErrorMessage = ApiError.Describe(ex, "Request failed."));
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    private async Task LoadPoliceAsync(CancellationToken ct)
    {
        var catalog = await _api.ListPackagesAsync(ct).ConfigureAwait(false);
        var list = await _api.ListPolicemenAsync(ct).ConfigureAwait(false);
        await EnsureStaffPoolAsync(ct).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _packageCatalog = catalog.ToList();
            ReplacePolicemen(list);
            OnPropertyChanged(nameof(HasPolicemen));
            OnPropertyChanged(nameof(ShowPoliceEmpty));
        });
    }

    private async Task EnsureStaffPoolAsync(CancellationToken ct)
    {
        var structure = await _api.GetStructureAsync(ct).ConfigureAwait(false);
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

    private void ApplyConfig(BusinessClockConfig cfg)
    {
        var id = string.IsNullOrWhiteSpace(cfg.TimeZoneId) ? "UTC" : cfg.TimeZoneId.Trim();
        // The whole app reads company time from this one clock (§8.2).
        _clock.Apply(cfg);
        SelectedTimeZone = FindOrAddTimeZone(id);
        CurrentTimeZoneLabel = id;
        CurrentBusinessLocal = BusinessClock.TryResolveTimeZone(id, out _)
            ? _clock.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "Unknown time zone";
    }

    private TimeZoneOption FindOrAddTimeZone(string id)
    {
        var existing = TimeZoneOptions.FirstOrDefault(
            o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var option = TimeZoneCatalog.Describe(id);
        TimeZoneOptions.Insert(0, option);
        return option;
    }

}
