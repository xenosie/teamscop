using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Teamscop.App.Services;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.ViewModels;

public enum AdminSection
{
    Staffs,
    Teams,
    Leaderboard,
    Settings
}

public enum StaffDetailSection
{
    Summary,
    Screenshot,
    BrowsingHistory,
    TimeTrack,
    AppHistory,
    Settings
}

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly LocalAgentStore _store;
    private string _apiBaseUrl;
    private bool _staffLoaded;
    private ConfigRealtimeClient? _realtime;
    private readonly HashSet<string> _packages = new(StringComparer.Ordinal);

    public MainViewModel(LocalAgentState state)
    {
        AppSessionStore.SetActive(state.Role);
        _store = AppSessionStore.Create(state.Role);
        _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
        ApplyState(state);
        Section = AdminSection.Staffs;
        TeamsBoard = new TeamsBoardViewModel(state.ApiBaseUrl);
        AppHistory = new AppHistoryViewModel(state.ApiBaseUrl);
        StaffSummary = new StaffSummaryViewModel(state.ApiBaseUrl);
        Screenshots = new ScreenshotGalleryViewModel(state.ApiBaseUrl);
        Browsing = new BrowsingHistoryViewModel(state.ApiBaseUrl);
        TimeTrack = new TimeTrackViewModel(state.ApiBaseUrl);
        StaffTrackingSettings = new StaffTrackingSettingsViewModel(state.ApiBaseUrl);
        Settings = new SettingsViewModel(state.ApiBaseUrl);
        PeriodFilter = new StaffPeriodFilterViewModel(state.ApiBaseUrl);
        PeriodFilter.FilterChanged += OnPeriodFilterChanged;
        _ = LoadCompanyAvatarAsync(state.CompanyAvatarUrl);
    }

    [ObservableProperty] private string _companyName = "Teamscop";
    [ObservableProperty] private string _adminName = string.Empty;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private AdminSection _section;
    [ObservableProperty] private Bitmap? _companyAvatar;
    [ObservableProperty] private bool _isStaffsExpanded;
    [ObservableProperty] private bool _isLoadingStaff;
    [ObservableProperty] private StaffListItemViewModel? _selectedStaff;
    [ObservableProperty] private StaffDetailSection _staffDetailSection = StaffDetailSection.Summary;
    [ObservableProperty] private bool _isAdminSession;
    [ObservableProperty] private bool _canManageTeams;
    [ObservableProperty] private bool _canShowSummary;
    [ObservableProperty] private bool _canShowScreenshot;
    [ObservableProperty] private bool _canShowBrowsing;
    [ObservableProperty] private bool _canShowTimeTrack;
    [ObservableProperty] private bool _canShowAppHistory;
    [ObservableProperty] private bool _canShowStaffTrackingSettings;
    [ObservableProperty] private string _roleLabel = "Admin";

    public ObservableCollection<StaffListItemViewModel> StaffItems { get; } = [];
    public TeamsBoardViewModel TeamsBoard { get; private set; } = null!;
    public AppHistoryViewModel AppHistory { get; private set; } = null!;
    public StaffSummaryViewModel StaffSummary { get; private set; } = null!;
    public ScreenshotGalleryViewModel Screenshots { get; private set; } = null!;
    public BrowsingHistoryViewModel Browsing { get; private set; } = null!;
    public TimeTrackViewModel TimeTrack { get; private set; } = null!;
    public StaffTrackingSettingsViewModel StaffTrackingSettings { get; private set; } = null!;
    public SettingsViewModel Settings { get; private set; } = null!;
    public StaffPeriodFilterViewModel PeriodFilter { get; private set; } = null!;

    public bool HasCompanyAvatar => CompanyAvatar is not null;
    public bool IsStaffs => Section == AdminSection.Staffs;
    public bool IsTeams => Section == AdminSection.Teams;
    public bool IsLeaderboard => Section == AdminSection.Leaderboard;
    public bool IsSettings => Section == AdminSection.Settings;
    public bool HasStaff => StaffItems.Count > 0;
    public bool ShowStaffEmpty => IsStaffsExpanded && !IsLoadingStaff && !HasStaff;
    public bool HasSelectedStaff => SelectedStaff is not null;
    public bool ShowTeamsNav => IsAdminSession || CanManageTeams;
    /// <summary>Deferred — Leaderboard UI not built yet.</summary>
    public bool ShowLeaderboardNav => false;
    public bool ShowCompanySettingsNav => IsAdminSession;

    public bool IsStaffDetailSummary => StaffDetailSection == StaffDetailSection.Summary;
    public bool IsStaffDetailScreenshot => StaffDetailSection == StaffDetailSection.Screenshot;
    public bool IsStaffDetailBrowsingHistory => StaffDetailSection == StaffDetailSection.BrowsingHistory;
    public bool IsStaffDetailTimeTrack => StaffDetailSection == StaffDetailSection.TimeTrack;
    public bool IsStaffDetailAppHistory => StaffDetailSection == StaffDetailSection.AppHistory;
    public bool IsStaffDetailSettings => StaffDetailSection == StaffDetailSection.Settings;

    public string Subtitle =>
        string.IsNullOrWhiteSpace(AdminName)
            ? $"{RoleLabel} dashboard"
            : $"{AdminName} · {RoleLabel}";

    partial void OnAdminNameChanged(string value)
        => OnPropertyChanged(nameof(Subtitle));

    partial void OnRoleLabelChanged(string value)
        => OnPropertyChanged(nameof(Subtitle));

    partial void OnIsAdminSessionChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowTeamsNav));
        OnPropertyChanged(nameof(ShowCompanySettingsNav));
        if (value)
        {
            CanManageTeams = true;
            CanShowScreenshot = true;
            CanShowTimeTrack = true;
            CanShowBrowsing = true;
            CanShowSummary = true;
            CanShowAppHistory = true;
            CanShowStaffTrackingSettings = true;
        }
    }

    partial void OnCanManageTeamsChanged(bool value)
        => OnPropertyChanged(nameof(ShowTeamsNav));

    partial void OnCompanyAvatarChanged(Bitmap? value)
        => OnPropertyChanged(nameof(HasCompanyAvatar));

    partial void OnSectionChanged(AdminSection value)
    {
        OnPropertyChanged(nameof(IsStaffs));
        OnPropertyChanged(nameof(IsTeams));
        OnPropertyChanged(nameof(IsLeaderboard));
        OnPropertyChanged(nameof(IsSettings));
        if (value == AdminSection.Teams)
        {
            if (!ShowTeamsNav)
            {
                Section = AdminSection.Staffs;
                return;
            }

            _ = TeamsBoard.LoadAsync();
        }
        else if (value == AdminSection.Settings)
        {
            if (!ShowCompanySettingsNav)
            {
                Section = AdminSection.Staffs;
                return;
            }

            _ = Settings.LoadAsync();
        }
    }

    partial void OnIsStaffsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowStaffEmpty));
        if (value)
        {
            _ = LoadStaffAsync(force: false);
        }
    }

    partial void OnIsLoadingStaffChanged(bool value)
        => OnPropertyChanged(nameof(ShowStaffEmpty));

    partial void OnSelectedStaffChanged(StaffListItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedStaff));
        if (value is null)
        {
            AppHistory.Reset();
            StaffSummary.Reset();
            Screenshots.Reset();
            Browsing.Reset();
            TimeTrack.Reset();
            StaffTrackingSettings.Reset();
            PeriodFilter.CloseCalendarCommand.Execute(null);
        }
        else
        {
            _ = PeriodFilter.EnsureClockAsync();
            ReloadStaffSectionData(force: true);
        }
    }

    partial void OnStaffDetailSectionChanged(StaffDetailSection value)
    {
        OnPropertyChanged(nameof(IsStaffDetailSummary));
        OnPropertyChanged(nameof(IsStaffDetailScreenshot));
        OnPropertyChanged(nameof(IsStaffDetailBrowsingHistory));
        OnPropertyChanged(nameof(IsStaffDetailTimeTrack));
        OnPropertyChanged(nameof(IsStaffDetailAppHistory));
        OnPropertyChanged(nameof(IsStaffDetailSettings));
        ReloadStaffSectionData(force: true);
    }

    private void OnPeriodFilterChanged()
    {
        ReloadStaffSectionData(force: true);
    }

    private void ReloadStaffSectionData(bool force)
    {
        if (SelectedStaff is null)
        {
            return;
        }

        if (StaffDetailSection == StaffDetailSection.Summary)
        {
            _ = StaffSummary.LoadAsync(
                SelectedStaff.UserId,
                SelectedStaff.Name,
                force,
                PeriodFilter.AppliedFromUtc,
                PeriodFilter.AppliedToUtc,
                PeriodFilter.AppliedStart,
                PeriodFilter.AppliedEnd);
        }
        else if (StaffDetailSection == StaffDetailSection.AppHistory)
        {
            _ = AppHistory.LoadAsync(
                SelectedStaff.UserId,
                force,
                PeriodFilter.AppliedFromUtc,
                PeriodFilter.AppliedToUtc);
        }
        else if (StaffDetailSection == StaffDetailSection.Screenshot)
        {
            _ = Screenshots.LoadAsync(
                SelectedStaff.UserId,
                force,
                PeriodFilter.AppliedFromUtc,
                PeriodFilter.AppliedToUtc);
        }
        else if (StaffDetailSection == StaffDetailSection.BrowsingHistory)
        {
            _ = Browsing.LoadAsync(
                SelectedStaff.UserId,
                force,
                PeriodFilter.AppliedFromUtc,
                PeriodFilter.AppliedToUtc);
        }
        else if (StaffDetailSection == StaffDetailSection.TimeTrack)
        {
            _ = TimeTrack.LoadAsync(
                SelectedStaff.UserId,
                force,
                PeriodFilter.AppliedFromUtc,
                PeriodFilter.AppliedToUtc,
                PeriodFilter.AppliedStart,
                PeriodFilter.AppliedEnd);
        }
        else if (StaffDetailSection == StaffDetailSection.Settings)
        {
            _ = StaffTrackingSettings.LoadAsync(
                SelectedStaff.UserId,
                SelectedStaff.Name,
                force);
        }
    }

    [RelayCommand]
    private void Navigate(string? sectionName)
    {
        if (!Enum.TryParse<AdminSection>(sectionName, ignoreCase: true, out var section))
        {
            return;
        }

        if (section == AdminSection.Teams && !ShowTeamsNav)
        {
            return;
        }

        if (section == AdminSection.Leaderboard && !ShowLeaderboardNav)
        {
            return;
        }

        if (section == AdminSection.Settings && !ShowCompanySettingsNav)
        {
            return;
        }

        Section = section;
        if (section != AdminSection.Staffs)
        {
            ClearSelectedStaff();
        }
    }

    private void ClearSelectedStaff()
    {
        if (SelectedStaff is null && !StaffItems.Any(s => s.IsSelected))
        {
            return;
        }

        foreach (var item in StaffItems)
        {
            item.IsSelected = false;
        }

        SelectedStaff = null;
        StaffDetailSection = StaffDetailSection.Summary;
        AppHistory.Reset();
        StaffSummary.Reset();
        Screenshots.Reset();
        Browsing.Reset();
        TimeTrack.Reset();
        StaffTrackingSettings.Reset();
    }

    [RelayCommand]
    private void ToggleStaffsExpanded()
    {
        IsStaffsExpanded = !IsStaffsExpanded;
    }

    [RelayCommand]
    private void SelectStaff(StaffListItemViewModel? staff)
    {
        if (staff is null)
        {
            return;
        }

        foreach (var item in StaffItems)
        {
            item.IsSelected = item.UserId == staff.UserId;
        }

        SelectedStaff = staff;
        StaffDetailSection = PickDefaultStaffDetailSection();
        Section = AdminSection.Staffs;
        IsStaffsExpanded = true;
    }

    [RelayCommand]
    private void NavigateStaffDetail(string? sectionName)
    {
        if (SelectedStaff is null)
        {
            return;
        }

        if (!Enum.TryParse<StaffDetailSection>(sectionName, ignoreCase: true, out var section))
        {
            return;
        }

        if (!IsStaffDetailSectionAllowed(section))
        {
            return;
        }

        StaffDetailSection = section;
    }

    public void ApplyState(LocalAgentState state)
    {
        AppSessionStore.SetActive(state.Role);
        IsAdminSession = AppSessionStore.IsAdminRole(state.Role);
        RoleLabel = IsAdminSession ? "Admin" : "Staff";
        CompanyName = string.IsNullOrWhiteSpace(state.CompanyName) ? "Teamscop" : state.CompanyName!;
        AdminName = state.Username ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(state.ApiBaseUrl))
        {
            _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
        }
    }

    public async Task RefreshProfileAsync()
    {
        var state = _store.Load();
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            return;
        }

        _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);

        try
        {
            using var api = new AuthApiClient(_apiBaseUrl);
            var me = await api.MeAsync(state.AccessToken);
            state.Username = me.Username;
            state.CompanyName = me.Company.Name;
            state.CompanyId = me.Company.Id;
            state.Role = me.Role;
            state.DeviceKey = me.DeviceKey;
            state.CompanyAvatarUrl = me.Company.AvatarUrl;
            _store.Save(state);
            ApplyState(state);
            await LoadCompanyAvatarAsync(state.CompanyAvatarUrl);
            await RefreshCapabilitiesAsync();
            await EnsureRealtimeAsync();
            await LoadStaffAsync(force: true);
            IsStaffsExpanded = true;

            StatusMessage = null;
        }
        catch
        {
            // Still try cached avatar URL.
            await LoadCompanyAvatarAsync(state.CompanyAvatarUrl);
            StatusMessage = null;
        }
    }

    public async Task EnsureRealtimeAsync()
    {
        var state = _store.Load();
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            return;
        }

        if (_realtime is not null)
        {
            return;
        }

        _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
        var client = new ConfigRealtimeClient(_apiBaseUrl);
        client.OrgStructureChanged += OnOrgStructureSignal;
        client.AuthoritiesChanged += OnAuthoritiesSignal;
        client.PolicemenChanged += OnPolicemenSignal;
        client.BusinessTimeChanged += OnBusinessTimeSignal;
        client.ReconnectedAsync += OnOrgChangedAsync;
        try
        {
            await client.StartAsync(state.AccessToken).ConfigureAwait(false);
            _realtime = client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnOrgStructureSignal(OrgStructureDto unused)
    {
        _ = OnOrgChangedAsync();
    }

    private void OnAuthoritiesSignal(EffectiveAuthoritiesDto auth)
    {
        _ = OnAuthoritiesChangedAsync(auth);
    }

    private void OnPolicemenSignal(IReadOnlyList<PolicemanDto> list)
    {
        Dispatcher.UIThread.Post(() => Settings.ApplyPolicemenRealtime(list));
    }

    private void OnBusinessTimeSignal(BusinessClockConfig cfg)
    {
        Dispatcher.UIThread.Post(() => Settings.ApplyBusinessTimeRealtime(cfg));
    }

    private async Task OnOrgChangedAsync()
    {
        await RefreshCapabilitiesAsync().ConfigureAwait(false);
        await LoadStaffAsync(force: true).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsStaffsExpanded = true;
            if (SelectedStaff is not null
                && StaffItems.All(s => s.UserId != SelectedStaff.UserId))
            {
                ClearSelectedStaff();
            }

            ClampStaffDetailSection();
        });
    }

    private async Task OnAuthoritiesChangedAsync(EffectiveAuthoritiesDto auth)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ApplyAuthorities(auth);
            ClampStaffDetailSection();
        });
    }

    private async Task RefreshCapabilitiesAsync()
    {
        var state = _store.Load();
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            return;
        }

        _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
        try
        {
            using var police = new PoliceApiClient(_apiBaseUrl);
            var auth = await police.GetMineAsync(state.AccessToken).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => ApplyAuthorities(auth));

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            if (_realtime is not null)
            {
                var placement = await _realtime.PullOrgPlacementAsync(http, state.AccessToken).ConfigureAwait(false);
                if (placement is not null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!IsAdminSession)
                        {
                            RoleLabel = placement.IsTeamLeader ? "Team leader" : "Staff";
                        }
                    });
                }
            }
            else
            {
                using var org = new OrgApiClient(_apiBaseUrl);
                var placement = await org.GetMyPlacementAsync(state.AccessToken).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!IsAdminSession)
                    {
                        RoleLabel = placement.IsTeamLeader ? "Team leader" : "Staff";
                    }
                });
            }
        }
        catch
        {
            // Keep last known capabilities.
        }
    }

    private void ApplyAuthorities(EffectiveAuthoritiesDto auth)
    {
        _packages.Clear();
        foreach (var p in auth.Packages)
        {
            _packages.Add(p);
        }

        IsAdminSession = auth.IsAdmin;
        CanManageTeams = auth.IsAdmin || _packages.Contains(AuthorityPackageIds.TeamManagement);
        CanShowScreenshot = auth.IsAdmin || _packages.Contains(AuthorityPackageIds.ViewScreenshot);
        CanShowTimeTrack = auth.IsAdmin || _packages.Contains(AuthorityPackageIds.ViewTimeTrack);
        CanShowBrowsing = auth.IsAdmin || _packages.Contains(AuthorityPackageIds.ViewBrowserHistory);
        CanShowSummary = CanShowTimeTrack || CanShowBrowsing;
        CanShowAppHistory = auth.IsAdmin
            || _packages.Contains(AuthorityPackageIds.UninstallApproval)
            || _packages.Contains(AuthorityPackageIds.UsbApproval);
        CanShowStaffTrackingSettings = auth.IsAdmin;
        if (auth.IsAdmin)
        {
            RoleLabel = "Admin";
        }

        ClampStaffDetailSection();
    }

    private bool IsStaffDetailSectionAllowed(StaffDetailSection section)
        => section switch
        {
            StaffDetailSection.Summary => CanShowSummary,
            StaffDetailSection.Screenshot => CanShowScreenshot,
            StaffDetailSection.BrowsingHistory => CanShowBrowsing,
            StaffDetailSection.TimeTrack => CanShowTimeTrack,
            StaffDetailSection.AppHistory => CanShowAppHistory,
            StaffDetailSection.Settings => CanShowStaffTrackingSettings,
            _ => false
        };

    private StaffDetailSection PickDefaultStaffDetailSection()
    {
        foreach (var section in new[]
                 {
                     StaffDetailSection.Summary,
                     StaffDetailSection.Screenshot,
                     StaffDetailSection.BrowsingHistory,
                     StaffDetailSection.TimeTrack,
                     StaffDetailSection.AppHistory,
                     StaffDetailSection.Settings
                 })
        {
            if (IsStaffDetailSectionAllowed(section))
            {
                return section;
            }
        }

        return StaffDetailSection.Summary;
    }

    private void ClampStaffDetailSection()
    {
        if (!IsStaffDetailSectionAllowed(StaffDetailSection))
        {
            StaffDetailSection = PickDefaultStaffDetailSection();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_realtime is not null)
        {
            await _realtime.DisposeAsync().ConfigureAwait(false);
            _realtime = null;
        }
    }

    public async Task LoadStaffAsync(bool force)
    {
        if (_staffLoaded && !force)
        {
            return;
        }

        var state = _store.Load();
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            return;
        }

        _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
        IsLoadingStaff = true;
        try
        {
            using var org = new OrgApiClient(_apiBaseUrl);
            var staff = await org.ListVisibleStaffAsync(state.AccessToken).ConfigureAwait(false);
            var items = new List<StaffListItemViewModel>(staff.Count);
            foreach (var s in staff)
            {
                items.Add(new StaffListItemViewModel
                {
                    UserId = s.UserId,
                    Name = s.Username,
                    AvatarUrl = s.AvatarUrl
                });
            }

            var selectedId = SelectedStaff?.UserId;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StaffItems.Clear();
                foreach (var item in items)
                {
                    item.IsSelected = selectedId.HasValue && item.UserId == selectedId.Value;
                    StaffItems.Add(item);
                }

                if (selectedId.HasValue)
                {
                    SelectedStaff = StaffItems.FirstOrDefault(s => s.UserId == selectedId.Value);
                }

                OnPropertyChanged(nameof(HasStaff));
                OnPropertyChanged(nameof(ShowStaffEmpty));
            });

            _staffLoaded = true;

            foreach (var item in items)
            {
                _ = LoadStaffAvatarAsync(item);
            }
        }
        catch
        {
            // Keep whatever list we already have.
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsLoadingStaff = false);
        }
    }

    private async Task LoadStaffAvatarAsync(StaffListItemViewModel item)
    {
        var absolute = ToAbsoluteUrl(item.AvatarUrl);
        if (absolute is null)
        {
            return;
        }

        try
        {
            byte[] bytes;
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
            {
                bytes = await http.GetByteArrayAsync(absolute).ConfigureAwait(false);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                using var ms = new MemoryStream(bytes);
                item.Avatar = new Bitmap(ms);
            });
        }
        catch
        {
            // Placeholder icon stays visible.
        }
    }

    private async Task LoadCompanyAvatarAsync(string? url)
    {
        var absolute = ToAbsoluteUrl(url);
        if (absolute is null)
        {
            await SetAvatarAsync(null);
            return;
        }

        try
        {
            byte[] bytes;
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
            {
                bytes = await http.GetByteArrayAsync(absolute).ConfigureAwait(false);
            }

            // Decode on UI thread — Avalonia bitmaps must be created there.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                using var ms = new MemoryStream(bytes);
                CompanyAvatar = new Bitmap(ms);
            });

        }
        catch
        {
            await SetAvatarAsync(null);
        }
    }

    private async Task SetAvatarAsync(Bitmap? bitmap)
    {
        await Dispatcher.UIThread.InvokeAsync(() => CompanyAvatar = bitmap);
    }

    private string? ToAbsoluteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        // "/media/..." is NOT an http URL — UriKind.Absolute would wrongly make file://
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs)
            && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
        {
            return abs.ToString();
        }

        return $"{_apiBaseUrl.TrimEnd('/')}/{url.TrimStart('/')}";
    }

    private static string ResolveApiBase(string? apiBaseUrl)
        => string.IsNullOrWhiteSpace(apiBaseUrl)
            ? Environment.GetEnvironmentVariable("TEAMSCOP_API_BASE") ?? "https://teamscop.com"
            : apiBaseUrl.TrimEnd('/');
}
