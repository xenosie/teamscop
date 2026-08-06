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

/// <summary>
/// Non-admin role shell: Leader / Police / Officer.
/// </summary>
public sealed partial class WorkspaceViewModel : ObservableObject, IAsyncDisposable
{
    private readonly LocalAgentStore _store;
    private readonly RoleShellKind _shellKind;
    private string _apiBaseUrl;
    private bool _staffLoaded;
    private ConfigRealtimeClient? _realtime;
    private readonly HashSet<string> _packages = new(StringComparer.Ordinal);

    public WorkspaceViewModel(LocalAgentState state, RoleShellKind shellKind)
    {
        _shellKind = shellKind;
        AppSessionStore.SetActive(state.Role);
        _store = AppSessionStore.Create(state.Role);
        _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
        ApplyState(state);
        RoleLabel = shellKind switch
        {
            RoleShellKind.Leader => "Team leader",
            RoleShellKind.Police => "Policeman",
            RoleShellKind.Officer => "Team leader · Policeman",
            _ => "Staff"
        };
        ApplyShellChrome();
        Section = WorkspaceSection.Staffs;
        IsStaffsExpanded = true;

        AppHistory = new AppHistoryViewModel(state.ApiBaseUrl);
        StaffSummary = new StaffSummaryViewModel(state.ApiBaseUrl);
        Screenshots = new ScreenshotGalleryViewModel(state.ApiBaseUrl);
        Browsing = new BrowsingHistoryViewModel(state.ApiBaseUrl);
        TimeTrack = new TimeTrackViewModel(state.ApiBaseUrl);
        PeriodFilter = new StaffPeriodFilterViewModel(state.ApiBaseUrl);
        PeriodFilter.FilterChanged += OnPeriodFilterChanged;
        TeamsBoard = new TeamsBoardViewModel(state.ApiBaseUrl);
        TotpCodes = new TotpCodesViewModel(state.ApiBaseUrl);
        _ = LoadCompanyAvatarAsync(state.CompanyAvatarUrl);
    }

    public RoleShellKind ShellKind => _shellKind;

    public ObservableCollection<StaffListItemViewModel> StaffItems { get; } = [];
    public AppHistoryViewModel AppHistory { get; }
    public StaffSummaryViewModel StaffSummary { get; }
    public ScreenshotGalleryViewModel Screenshots { get; }
    public BrowsingHistoryViewModel Browsing { get; }
    public TimeTrackViewModel TimeTrack { get; }
    public StaffPeriodFilterViewModel PeriodFilter { get; }
    public TeamsBoardViewModel TeamsBoard { get; }
    public TotpCodesViewModel TotpCodes { get; }

    [ObservableProperty] private string _companyName = "Teamscop";
    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string _roleLabel = "Staff";
    [ObservableProperty] private string _staffListTitle = "Staff";
    [ObservableProperty] private string _emptyStaffMessage = "No staff yet";
    [ObservableProperty] private string? _teamName;
    [ObservableProperty] private Bitmap? _companyAvatar;
    [ObservableProperty] private WorkspaceSection _section;
    [ObservableProperty] private bool _isStaffsExpanded = true;
    [ObservableProperty] private bool _isLoadingStaff;
    [ObservableProperty] private StaffListItemViewModel? _selectedStaff;
    [ObservableProperty] private StaffDetailSection _staffDetailSection = StaffDetailSection.Summary;
    [ObservableProperty] private bool _canManageTeams;
    [ObservableProperty] private bool _canShowCodes;
    [ObservableProperty] private bool _canShowSummary;
    [ObservableProperty] private bool _canShowScreenshot;
    [ObservableProperty] private bool _canShowBrowsing;
    [ObservableProperty] private bool _canShowTimeTrack;
    [ObservableProperty] private bool _canShowAppHistory;
    [ObservableProperty] private bool _isTeamLeader;

    public bool HasCompanyAvatar => CompanyAvatar is not null;
    public bool HasTeamName => !string.IsNullOrWhiteSpace(TeamName);
    public bool IsStaffs => Section == WorkspaceSection.Staffs;
    public bool IsCodes => Section == WorkspaceSection.Codes;
    public bool IsTeams => Section == WorkspaceSection.Teams;
    public bool HasStaff => StaffItems.Count > 0;
    public bool ShowStaffEmpty => IsStaffsExpanded && !IsLoadingStaff && !HasStaff;
    public bool HasSelectedStaff => SelectedStaff is not null && IsStaffs;
    public bool ShowStaffPrompt => IsStaffs && SelectedStaff is null;
    public bool ShowTeamsNav => CanManageTeams;
    public bool ShowCodesNav => CanShowCodes;
    public bool ShowAppHistoryTab => CanShowAppHistory && _shellKind != RoleShellKind.Leader;

    public bool IsStaffDetailSummary => StaffDetailSection == StaffDetailSection.Summary;
    public bool IsStaffDetailScreenshot => StaffDetailSection == StaffDetailSection.Screenshot;
    public bool IsStaffDetailBrowsingHistory => StaffDetailSection == StaffDetailSection.BrowsingHistory;
    public bool IsStaffDetailTimeTrack => StaffDetailSection == StaffDetailSection.TimeTrack;
    public bool IsStaffDetailAppHistory => StaffDetailSection == StaffDetailSection.AppHistory;

    public string Subtitle =>
        string.IsNullOrWhiteSpace(UserName)
            ? RoleLabel
            : HasTeamName
                ? $"{UserName} · {RoleLabel} · {TeamName}"
                : $"{UserName} · {RoleLabel}";

    partial void OnUserNameChanged(string value) => OnPropertyChanged(nameof(Subtitle));
    partial void OnRoleLabelChanged(string value) => OnPropertyChanged(nameof(Subtitle));
    partial void OnTeamNameChanged(string? value)
    {
        OnPropertyChanged(nameof(HasTeamName));
        OnPropertyChanged(nameof(Subtitle));
    }

    partial void OnCompanyAvatarChanged(Bitmap? value)
        => OnPropertyChanged(nameof(HasCompanyAvatar));

    partial void OnCanManageTeamsChanged(bool value)
        => OnPropertyChanged(nameof(ShowTeamsNav));

    partial void OnCanShowCodesChanged(bool value)
        => OnPropertyChanged(nameof(ShowCodesNav));

    partial void OnCanShowAppHistoryChanged(bool value)
        => OnPropertyChanged(nameof(ShowAppHistoryTab));

    partial void OnSectionChanged(WorkspaceSection value)
    {
        OnPropertyChanged(nameof(IsStaffs));
        OnPropertyChanged(nameof(IsCodes));
        OnPropertyChanged(nameof(IsTeams));
        OnPropertyChanged(nameof(HasSelectedStaff));
        OnPropertyChanged(nameof(ShowStaffPrompt));
        if (value == WorkspaceSection.Teams)
        {
            _ = TeamsBoard.LoadAsync();
        }
        else if (value == WorkspaceSection.Codes)
        {
            _ = TotpCodes.LoadAsync();
        }

        if (value != WorkspaceSection.Staffs)
        {
            ClearSelectedStaff();
        }
    }

    partial void OnIsStaffsExpandedChanged(bool value)
        => OnPropertyChanged(nameof(ShowStaffEmpty));

    partial void OnIsLoadingStaffChanged(bool value)
        => OnPropertyChanged(nameof(ShowStaffEmpty));

    partial void OnSelectedStaffChanged(StaffListItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedStaff));
        OnPropertyChanged(nameof(ShowStaffPrompt));
        if (value is null)
        {
            AppHistory.Reset();
            StaffSummary.Reset();
            Screenshots.Reset();
            Browsing.Reset();
            TimeTrack.Reset();
            PeriodFilter.CloseCalendarCommand.Execute(null);
        }
        else
        {
            _ = PeriodFilter.EnsureClockAsync();
            ReloadStaffSectionData();
        }
    }

    partial void OnStaffDetailSectionChanged(StaffDetailSection value)
    {
        OnPropertyChanged(nameof(IsStaffDetailSummary));
        OnPropertyChanged(nameof(IsStaffDetailScreenshot));
        OnPropertyChanged(nameof(IsStaffDetailBrowsingHistory));
        OnPropertyChanged(nameof(IsStaffDetailTimeTrack));
        OnPropertyChanged(nameof(IsStaffDetailAppHistory));
        ReloadStaffSectionData();
    }

    private void ApplyShellChrome()
    {
        switch (_shellKind)
        {
            case RoleShellKind.Leader:
                StaffListTitle = "My team";
                EmptyStaffMessage = "No team members yet";
                if (string.IsNullOrWhiteSpace(RoleLabel))
                {
                    RoleLabel = "Team leader";
                }

                break;
            case RoleShellKind.Police:
                StaffListTitle = "Company staff";
                EmptyStaffMessage = "No staff yet";
                if (string.IsNullOrWhiteSpace(RoleLabel))
                {
                    RoleLabel = "Policeman";
                }

                break;
            case RoleShellKind.Officer:
                StaffListTitle = "Company staff";
                EmptyStaffMessage = "No staff yet";
                if (string.IsNullOrWhiteSpace(RoleLabel) || RoleLabel == "Staff")
                {
                    RoleLabel = "Team leader · Policeman";
                }

                break;
        }
    }

    private void OnPeriodFilterChanged() => ReloadStaffSectionData();

    private void ReloadStaffSectionData()
    {
        if (SelectedStaff is null || !IsStaffs)
        {
            return;
        }

        if (StaffDetailSection == StaffDetailSection.Summary)
        {
            _ = StaffSummary.LoadAsync(
                SelectedStaff.UserId, SelectedStaff.Name, force: true,
                PeriodFilter.AppliedFromUtc, PeriodFilter.AppliedToUtc,
                PeriodFilter.AppliedStart, PeriodFilter.AppliedEnd);
        }
        else if (StaffDetailSection == StaffDetailSection.AppHistory)
        {
            _ = AppHistory.LoadAsync(
                SelectedStaff.UserId, force: true,
                PeriodFilter.AppliedFromUtc, PeriodFilter.AppliedToUtc);
        }
        else if (StaffDetailSection == StaffDetailSection.Screenshot)
        {
            _ = Screenshots.LoadAsync(
                SelectedStaff.UserId, force: true,
                PeriodFilter.AppliedFromUtc, PeriodFilter.AppliedToUtc);
        }
        else if (StaffDetailSection == StaffDetailSection.BrowsingHistory)
        {
            _ = Browsing.LoadAsync(
                SelectedStaff.UserId, force: true,
                PeriodFilter.AppliedFromUtc, PeriodFilter.AppliedToUtc);
        }
        else if (StaffDetailSection == StaffDetailSection.TimeTrack)
        {
            _ = TimeTrack.LoadAsync(
                SelectedStaff.UserId, force: true,
                PeriodFilter.AppliedFromUtc, PeriodFilter.AppliedToUtc,
                PeriodFilter.AppliedStart, PeriodFilter.AppliedEnd);
        }
    }

    [RelayCommand]
    private void Navigate(string? sectionName)
    {
        if (!Enum.TryParse<WorkspaceSection>(sectionName, ignoreCase: true, out var section))
        {
            return;
        }

        if (section == WorkspaceSection.Codes && !ShowCodesNav)
        {
            return;
        }

        if (section == WorkspaceSection.Teams && !ShowTeamsNav)
        {
            return;
        }

        Section = section;
    }

    [RelayCommand]
    private void ToggleStaffsExpanded() => IsStaffsExpanded = !IsStaffsExpanded;

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
        Section = WorkspaceSection.Staffs;
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

    private void ClearSelectedStaff()
    {
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
    }

    public void ApplyState(LocalAgentState state)
    {
        AppSessionStore.SetActive(state.Role);
        CompanyName = string.IsNullOrWhiteSpace(state.CompanyName) ? "Teamscop" : state.CompanyName!;
        UserName = state.Username ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(state.ApiBaseUrl))
        {
            _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
        }
    }

    public async Task InitializeAsync()
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
        }
        catch
        {
            await LoadCompanyAvatarAsync(state.CompanyAvatarUrl);
        }
    }

    public async Task EnsureRealtimeAsync()
    {
        var state = _store.Load();
        if (string.IsNullOrWhiteSpace(state.AccessToken) || _realtime is not null)
        {
            return;
        }

        _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
        var client = new ConfigRealtimeClient(_apiBaseUrl);
        client.OrgStructureChanged += OnOrgStructureSignal;
        client.AuthoritiesChanged += OnAuthoritiesSignal;
        client.ReconnectedAsync += OnRealtimeRefreshAsync;
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
        => _ = OnRealtimeRefreshAsync();

    private void OnAuthoritiesSignal(EffectiveAuthoritiesDto auth)
    {
        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            ApplyAuthorities(auth);
            ClampStaffDetailSection();
        });
    }

    private async Task OnRealtimeRefreshAsync()
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
            using var org = new OrgApiClient(_apiBaseUrl);
            var placement = await org.GetMyPlacementAsync(state.AccessToken).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyAuthorities(auth);
                IsTeamLeader = placement.IsTeamLeader;
                TeamName = placement.TeamName;
                if (_shellKind == RoleShellKind.Officer)
                {
                    RoleLabel = "Team leader · Policeman";
                }
                else if (_shellKind == RoleShellKind.Leader || placement.IsTeamLeader)
                {
                    RoleLabel = "Team leader";
                }
                else if (auth.IsPoliceman || CanShowCodes)
                {
                    RoleLabel = "Policeman";
                }

                ApplyShellChrome();
            });
        }
        catch
        {
            // keep last known
        }
    }

    private void ApplyAuthorities(EffectiveAuthoritiesDto auth)
    {
        _packages.Clear();
        foreach (var p in auth.Packages)
        {
            _packages.Add(p);
        }

        var hasUsb = _packages.Contains(AuthorityPackageIds.UsbApproval);
        var hasUninstall = _packages.Contains(AuthorityPackageIds.UninstallApproval);

        CanManageTeams = _packages.Contains(AuthorityPackageIds.TeamManagement);
        // Codes: policemen with usb_approval and/or uninstall_approval can generate codes for staff.
        CanShowCodes = (_shellKind is RoleShellKind.Police or RoleShellKind.Officer)
                       && (hasUsb || hasUninstall);

        CanShowScreenshot = _packages.Contains(AuthorityPackageIds.ViewScreenshot);
        CanShowTimeTrack = _packages.Contains(AuthorityPackageIds.ViewTimeTrack);
        CanShowBrowsing = _packages.Contains(AuthorityPackageIds.ViewBrowserHistory);
        CanShowSummary = CanShowTimeTrack || CanShowBrowsing;
        CanShowAppHistory = _shellKind != RoleShellKind.Leader
                            && (hasUsb || hasUninstall);

        if (Section == WorkspaceSection.Codes && !ShowCodesNav)
        {
            Section = WorkspaceSection.Staffs;
        }

        if (Section == WorkspaceSection.Teams && !ShowTeamsNav)
        {
            Section = WorkspaceSection.Staffs;
        }

        ClampStaffDetailSection();
        OnPropertyChanged(nameof(ShowAppHistoryTab));
    }

    private bool IsStaffDetailSectionAllowed(StaffDetailSection section)
        => section switch
        {
            StaffDetailSection.Summary => CanShowSummary,
            StaffDetailSection.Screenshot => CanShowScreenshot,
            StaffDetailSection.BrowsingHistory => CanShowBrowsing,
            StaffDetailSection.TimeTrack => CanShowTimeTrack,
            StaffDetailSection.AppHistory => ShowAppHistoryTab,
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
                     StaffDetailSection.AppHistory
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
            var items = staff.Select(s => new StaffListItemViewModel
            {
                UserId = s.UserId,
                Name = s.Username,
                AvatarUrl = s.AvatarUrl
            }).ToList();

            var selectedId = SelectedStaff?.UserId;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StaffItems.Clear();
                foreach (var item in items)
                {
                    item.IsSelected = selectedId.HasValue && item.UserId == selectedId.Value;
                    StaffItems.Add(item);
                }

                SelectedStaff = selectedId.HasValue
                    ? StaffItems.FirstOrDefault(s => s.UserId == selectedId.Value)
                    : null;
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
            // keep list
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsLoadingStaff = false);
        }
    }

    private async Task LoadStaffAvatarAsync(StaffListItemViewModel item)
    {
        var absolute = ToAbsoluteUrl(item.AvatarUrl);
        if (absolute is null) return;
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
            // placeholder
        }
    }

    private async Task LoadCompanyAvatarAsync(string? url)
    {
        var absolute = ToAbsoluteUrl(url);
        if (absolute is null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => CompanyAvatar = null);
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
                CompanyAvatar = new Bitmap(ms);
            });
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() => CompanyAvatar = null);
        }
    }

    private string? ToAbsoluteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
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

    public async ValueTask DisposeAsync()
    {
        if (_realtime is not null)
        {
            await _realtime.DisposeAsync().ConfigureAwait(false);
            _realtime = null;
        }
    }
}
