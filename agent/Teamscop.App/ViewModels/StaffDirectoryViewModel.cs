using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Teamscop.App.Composition;
using Teamscop.App.Services;

namespace Teamscop.App.ViewModels;

/// <summary>
/// The staff the caller may see, scoped server-side by role (§4.5 excludes self). A 20–50 member
/// company is paged 25 at a time and filtered by name (§15.1), and each row's avatar is fetched
/// only once the row is actually on screen — the old loader fired one request per member at once.
/// </summary>
public sealed partial class StaffDirectoryViewModel : ObservableObject
{
    private const int PageSize = 25;

    /// <summary>§5.1 — presence badges refresh on a light poll while the roster is visible (§15.2).</summary>
    private static readonly TimeSpan PresenceInterval = TimeSpan.FromSeconds(45);

    private readonly TeamscopApi _api;
    private readonly SessionStore _session;
    private readonly ImageLoader _images;
    private readonly UiLog _log;
    private readonly PageWindow<StaffListItemViewModel> _page;
    private readonly DispatcherTimer _presenceTimer;
    private bool _presenceActive;

    /// <summary>
    /// Route entry, the nav chevron and a server push can now all ask for a refresh within
    /// milliseconds of each other. Without this they would each fire a request and the last
    /// response to land — not the newest — would win.
    /// </summary>
    private readonly SemaphoreSlim _reload = new(1, 1);

    private List<StaffListItemViewModel> _all = [];
    private bool _loaded;

    public StaffDirectoryViewModel(AppServices services)
    {
        _api = services.Api;
        _session = services.Session;
        _images = services.Images;
        _log = services.Log;
        _page = new PageWindow<StaffListItemViewModel>(Items, PageSize);
        _presenceTimer = new DispatcherTimer { Interval = PresenceInterval };
        _presenceTimer.Tick += OnPresenceTick;
    }

    /// <summary>Raised on the UI thread when the user picks a different member (or none).</summary>
    public event Action<StaffListItemViewModel?>? SelectionChanged;

    /// <summary>The page window the nav renders — never the whole company.</summary>
    public ObservableCollection<StaffListItemViewModel> Items { get; } = [];

    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _title = "Staff";
    [ObservableProperty] private string _emptyMessage = "No staff yet";
    [ObservableProperty] private StaffListItemViewModel? _selected;
    [ObservableProperty] private string _filterText = string.Empty;

    /// <summary>
    /// Defect 3 — a failed load used to be indistinguishable from an empty company. The nav said
    /// "No staff yet" whether the roster was genuinely empty or the request had thrown, and the
    /// only trace was a diagnostics line that expired after five minutes.
    /// </summary>
    [ObservableProperty] private string? _loadError;

    public bool HasItems => Items.Count > 0;
    public bool ShowEmpty => IsExpanded && !IsLoading && !HasItems && LoadError is null;
    public bool ShowError => IsExpanded && !IsLoading && LoadError is not null;
    public bool ShowList => IsExpanded && HasItems;
    public bool ShowLoading => IsExpanded && IsLoading;

    /// <summary>The filter box only earns its space once there is enough to search through.</summary>
    public bool ShowFilter => IsExpanded && _all.Count > PageSize / 2;

    public bool ShowPaging => IsExpanded && _page.IsPaged;
    public bool HasMore => _page.HasMore;
    public string PageLabel => _page.Label;

    /// <summary>Leaders see their own team, everyone else the company (§4.7).</summary>
    public void ApplyCapabilities(ShellCapabilities capabilities)
    {
        var leaderOnly = capabilities is { IsTeamLeader: true, IsAdmin: false, IsPoliceman: false };
        Title = leaderOnly ? "My team" : "Staff";
        EmptyMessage = leaderOnly ? "No team members yet" : "No staff yet";
    }

    /// <summary>
    /// Re-reads the roster. <paramref name="force"/> is how a member who enrolled after this window
    /// opened ever appears: the un-forced path latches after the first success and, before defect 3
    /// was fixed, cold start was its only caller — so an admin with the app already open watched an
    /// empty dropdown until they restarted (§4.1, §14.1).
    /// </summary>
    public async Task ReloadAsync(bool force, CancellationToken ct = default)
    {
        if ((_loaded && !force) || !_session.HasToken)
        {
            return;
        }

        // Serialised, not dropped: whoever asked second still gets a list no older than their ask.
        try
        {
            await _reload.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Route entry starts this on the route's token, so navigating away while another
            // refresh holds the gate is ordinary. It is not a failure and must not be logged as one.
            return;
        }

        try
        {
            if (_loaded && !force)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => IsLoading = true);
            try
            {
                var staff = await _api.ListVisibleStaffAsync(ct).ConfigureAwait(false);

                // §4.5 / defect 8 — the server already excludes the viewer, and this makes it
                // impossible for a future scoping change to put the admin in their own roster and
                // present them to themselves as a monitored subject.
                var selfId = _session.State.UserId;
                var items = staff
                    .Where(s => selfId is null || s.UserId != selfId.Value)
                    .Select(s => new StaffListItemViewModel
                    {
                        UserId = s.UserId,
                        Name = s.Username,
                        AvatarUrl = s.AvatarUrl
                    })
                    .ToList();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    LoadError = null;
                    Replace(items);
                });
                _loaded = true;

                // §5.1 — badge the roster the moment it lands, not only on the next 45 s tick.
                if (_presenceActive)
                {
                    RefreshPresenceAsync().FireAndForget(_log, "Staff presence");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The window is closing; whatever is on screen stays until it goes.
            }
            catch (Exception ex)
            {
                // Keep whatever list is on screen rather than emptying the nav under the user —
                // but say so, and offer the retry the old silent catch never did.
                _log.Warn("Staff list refresh failed", ex);
                await Dispatcher.UIThread.InvokeAsync(
                    () => LoadError = ApiError.Describe(ex, "Could not load staff."));
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
            }
        }
        finally
        {
            _reload.Release();
        }
    }

    /// <summary>The retry button under the error line — always forced, never the latched path.</summary>
    [RelayCommand]
    private Task RetryAsync() => ReloadAsync(force: true);

    // ---- §5.1 presence badges -------------------------------------------------------------------

    /// <summary>
    /// Begin (or resume) live presence badges — called when the caller can see staff data. The
    /// immediate refresh badges a roster that is already on screen; the timer keeps it current while
    /// the list is visible. One request covers every visible member (§15.2).
    /// </summary>
    public void StartPresence()
    {
        if (!_presenceActive)
        {
            _presenceActive = true;
            _presenceTimer.Start();
        }

        RefreshPresenceAsync().FireAndForget(_log, "Staff presence");
    }

    /// <summary>Stops the poll — on sign-out, on losing staff visibility, and on shutdown.</summary>
    public void StopPresence()
    {
        _presenceActive = false;
        _presenceTimer.Stop();
    }

    /// <summary>
    /// Merges the server's coarse presence onto each row by user id. A member the server did not
    /// name reads as offline; a failed fetch leaves the last known badges rather than blanking them,
    /// because a badge is decoration and problems never surface as an alert (§12.3).
    /// </summary>
    public async Task RefreshPresenceAsync(CancellationToken ct = default)
    {
        if (!_session.HasToken || _all.Count == 0)
        {
            return;
        }

        try
        {
            var presence = await _api.GetPresenceAsync(ct).ConfigureAwait(false);
            var map = presence.ToDictionary(p => p.UserId);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var item in _all)
                {
                    if (map.TryGetValue(item.UserId, out var row))
                    {
                        item.Presence = row.State;
                        item.AgentStatus = row.Status;
                        item.AgentStatusReason = row.StatusReason;
                    }
                    else
                    {
                        // Absent from the response means the caller may not see that row's badge.
                        // Say nothing rather than guess: an unknown status draws no dot at all.
                        item.Presence = StaffPresenceState.Offline;
                        item.AgentStatus = StaffAgentStatusState.Unknown;
                        item.AgentStatusReason = null;
                    }
                }
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The window is closing; the badges on screen stay until it does.
        }
        catch (Exception ex)
        {
            _log.Debug($"Staff presence not loaded: {ApiError.Describe(ex)}");
        }
    }

    private void OnPresenceTick(object? sender, EventArgs e)
        => RefreshPresenceAsync().FireAndForget(_log, "Staff presence");

    [RelayCommand]
    public void Select(StaffListItemViewModel? staff)
    {
        foreach (var item in _all)
        {
            item.IsSelected = staff is not null && item.UserId == staff.UserId;
        }

        Selected = staff;
        IsExpanded = true;
        if (staff is not null)
        {
            EnsureVisible(staff);
        }

        SelectionChanged?.Invoke(staff);
    }

    /// <summary>Selects by id for a drill-down from Today. False when the member is not visible.</summary>
    public bool SelectById(Guid staffUserId)
    {
        var match = _all.FirstOrDefault(s => s.UserId == staffUserId);
        if (match is null)
        {
            return false;
        }

        Select(match);
        return true;
    }

    public void Clear() => Select(null);

    public void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void ShowMore()
    {
        _page.More();
        RaisePaging();
    }

    [RelayCommand]
    private void ShowAll()
    {
        _page.All();
        RaisePaging();
    }

    /// <summary>Called by the view as a row is realised, so only visible avatars are fetched.</summary>
    public void EnsureAvatar(StaffListItemViewModel item)
    {
        if (item.AvatarRequested || item.Avatar is not null || string.IsNullOrWhiteSpace(item.AvatarUrl))
        {
            return;
        }

        item.AvatarRequested = true;
        LoadAvatarAsync(item).FireAndForget(_log, "Staff avatar");
    }

    /// <summary>Keeps the current selection if the member survived the refresh; drops it if not.</summary>
    private void Replace(IReadOnlyList<StaffListItemViewModel> items)
    {
        var selectedId = Selected?.UserId;
        _all = items.ToList();
        foreach (var item in _all)
        {
            item.IsSelected = selectedId.HasValue && item.UserId == selectedId.Value;
        }

        ApplyFilter();

        if (!selectedId.HasValue)
        {
            return;
        }

        var match = _all.FirstOrDefault(s => s.UserId == selectedId.Value);
        if (match is null)
        {
            Select(null);
        }
        else
        {
            Selected = match;
            EnsureVisible(match);
        }
    }

    private void ApplyFilter()
    {
        var filter = FilterText.Trim();
        var source = string.IsNullOrEmpty(filter)
            ? _all
            : _all.Where(s => s.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();

        _page.Reset(source);
        RaisePaging();
        RaiseList();
    }

    /// <summary>A member picked from elsewhere may sit behind the filter or past the current page.</summary>
    private void EnsureVisible(StaffListItemViewModel item)
    {
        if (Items.Contains(item))
        {
            return;
        }

        if (FilterText.Length > 0)
        {
            // Clearing the filter re-runs ApplyFilter, which may already bring the row into view.
            FilterText = string.Empty;
            if (Items.Contains(item))
            {
                return;
            }
        }

        _page.EnsureVisible(item);
        RaisePaging();
        RaiseList();
    }

    private async Task LoadAvatarAsync(StaffListItemViewModel item)
    {
        var bitmap = await _images.LoadAsync(item.AvatarUrl).ConfigureAwait(false);
        if (bitmap is null)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() => item.Avatar = bitmap);
    }

    private void RaisePaging()
    {
        OnPropertyChanged(nameof(ShowPaging));
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(ShowFilter));
    }

    private void RaiseList()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowList));
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnIsExpandedChanged(bool value)
    {
        RaiseList();
        RaisePaging();
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(ShowError));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(ShowError));
    }

    partial void OnLoadErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowError));
    }
}
