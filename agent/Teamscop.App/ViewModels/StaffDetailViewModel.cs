using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeroIconsAvalonia.Enums;
using Teamscop.App.Composition;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.ViewModels;

/// <summary>
/// One staff member across the six sub-sections, gated by <see cref="ShellCapabilities"/> and
/// filtered by one shared period (§14.3). The six-branch reload chain, the allowed/default/clamp
/// trio and the section nav all read the single <see cref="Sections"/> list — each of which
/// existed twice, and had already diverged, in MainViewModel and WorkspaceViewModel.
/// </summary>
public sealed partial class StaffDetailViewModel : ObservableObject
{
    private readonly UiLog _log;
    private CancellationToken _lifetime = CancellationToken.None;

    /// <summary>
    /// Scopes the open section for as long as it is the open section — not just its first load.
    /// The gallery keeps this token to cancel "load older captures", so it must stay alive until
    /// the selection, the section or the route changes.
    /// </summary>
    private CancellationTokenSource? _sectionCts;

    private bool _isOpen;
    private bool _reloadOnOpen;

    public StaffDetailViewModel(AppServices services)
    {
        _log = services.Log;
        Summary = new StaffSummaryViewModel(services);
        Screenshots = new ScreenshotGalleryViewModel(services);
        Browsing = new BrowsingHistoryViewModel(services);
        TimeTrack = new TimeTrackViewModel(services);
        AppHistory = new AppHistoryViewModel(services);
        TrackingSettings = new StaffTrackingSettingsViewModel(services);
        PeriodFilter = new StaffPeriodFilterViewModel(services);
        PeriodFilter.FilterChanged += ReloadCurrentSection;
        // §3.4 — the screenshot viewer opens full-screen at shell level, so the request travels up.
        Screenshots.OpenViewerRequested += (metas, index) => OpenScreenshotViewerRequested?.Invoke(metas, index);

        Sections =
        [
            new StaffSectionViewModel(
                StaffSectionId.Summary, "Summary", IconType.DocumentText,
                c => c.CanSeeAnyStaffData, Summary),
            new StaffSectionViewModel(
                StaffSectionId.Screenshot, "Screenshot", IconType.Camera,
                c => c.CanViewScreenshot, Screenshots),
            new StaffSectionViewModel(
                StaffSectionId.Browsing, "Browsing History", IconType.GlobeAlt,
                c => c.CanViewBrowsing, Browsing),
            new StaffSectionViewModel(
                StaffSectionId.TimeTrack, "Time Track", IconType.Clock,
                c => c.CanViewTimeTrack, TimeTrack),
            new StaffSectionViewModel(
                StaffSectionId.AppHistory, "App history", IconType.QueueList,
                c => c.CanViewAppHistory, AppHistory),
            new StaffSectionViewModel(
                StaffSectionId.TrackingSettings, "Settings", IconType.Cog6Tooth,
                c => c.CanEditStaffTracking, TrackingSettings)
        ];
    }

    /// <summary>§3.4 — raised (page, start index) when a thumbnail is clicked; the shell opens the viewer.</summary>
    public event Action<IReadOnlyList<ScreenshotMetaItem>, int>? OpenScreenshotViewerRequested;

    public StaffSummaryViewModel Summary { get; }
    public ScreenshotGalleryViewModel Screenshots { get; }
    public BrowsingHistoryViewModel Browsing { get; }
    public TimeTrackViewModel TimeTrack { get; }
    public AppHistoryViewModel AppHistory { get; }
    public StaffTrackingSettingsViewModel TrackingSettings { get; }
    public StaffPeriodFilterViewModel PeriodFilter { get; }

    public IReadOnlyList<StaffSectionViewModel> Sections { get; }

    /// <summary>The subset the caller's packages reveal — the staff-section nav binds to this.</summary>
    public ObservableCollection<StaffSectionViewModel> VisibleSections { get; } = [];

    [ObservableProperty] private StaffListItemViewModel? _staff;
    [ObservableProperty] private StaffSectionViewModel? _current;

    private ShellCapabilities _capabilities = ShellCapabilities.None;

    public bool IsStaffSelected => Staff is not null;

    /// <summary>
    /// The section column belongs to the Staff route. The selection itself outlives leaving the
    /// route — returning restores the member and their six loaded sections — so what hides here is
    /// the nav, not the state behind it.
    /// </summary>
    public bool ShowSectionNav => _isOpen && IsStaffSelected;

    /// <summary>What the content host renders — null between selections, never a null-path binding.</summary>
    public object? CurrentContent => Current?.Content;

    // Flattened so the section nav binds to a never-null source: a "Staff.Name" path logs a
    // binding error on every deselect, and the avatar arrives after the row is already on screen.
    public string StaffName => Staff?.Name ?? string.Empty;
    public Bitmap? StaffAvatar => Staff?.Avatar;
    public bool StaffHasAvatar => Staff?.Avatar is not null;

    /// <summary>The shell's lifetime token: a section load in flight when the window closes stops.</summary>
    public void SetLifetime(CancellationToken token) => _lifetime = token;

    /// <summary>Selects a member and opens the first section their packages allow.</summary>
    public void Show(StaffListItemViewModel? staff)
    {
        if (staff is null)
        {
            Clear();
            return;
        }

        // Re-selecting the member already on screen keeps their loaded sections instead of
        // re-fetching all of them — this is what makes leaving and re-entering the route free.
        if (Staff is { } current && current.UserId == staff.UserId)
        {
            Staff = staff;
            return;
        }

        Staff = staff;
        SetCurrent(PickDefault());
        // §2.5 — open on today's period so the timetrack bar (and every section) has a concrete
        // span from the first frame. The empty "select a period" state was the whole reason the bar
        // showed nothing once Today's drill-down (its only period-applying entry) was deleted (§1.3).
        PeriodFilter.EnsureDefaultPeriod();
        ReloadCurrentSection();
    }

    public void Clear()
    {
        CancelSection();
        _reloadOnOpen = false;
        Staff = null;
        SetCurrent(null);
        PeriodFilter.CloseCalendarCommand.Execute(null);
        Summary.Reset();
        Screenshots.Reset();
        Browsing.Reset();
        TimeTrack.Reset();
        AppHistory.Reset();
        TrackingSettings.Reset();
    }

    /// <summary>Entering the Staff route. Whatever was selected is still selected and still loaded.</summary>
    public void Open()
    {
        _isOpen = true;
        OnPropertyChanged(nameof(ShowSectionNav));
        if (_reloadOnOpen)
        {
            ReloadCurrentSection();
        }
    }

    /// <summary>
    /// Leaving the Staff route. The selection and every loaded section survive — coming back
    /// restores what was on screen — but nothing stays in flight against a route nobody is
    /// looking at, so a late response cannot repaint over newer data.
    /// </summary>
    public void Suspend()
    {
        _isOpen = false;
        OnPropertyChanged(nameof(ShowSectionNav));
        // Cancelling retires the open section's token, so the section it belongs to is re-run on
        // the way back in — both to replace whatever a cut-short load left behind and to hand the
        // gallery a live token for its paging. The other five sections keep what they hold.
        _reloadOnOpen |= Staff is not null;
        CancelSection();
        PeriodFilter.CloseCalendarCommand.Execute(null);
    }

    /// <summary>
    /// §8.2 — the company zone moved, so every period bound and label is re-derived. A route that
    /// is not on screen reloads when it comes back rather than costing a request now.
    /// </summary>
    public void ReapplyClock()
    {
        PeriodFilter.ReapplyClock();
        if (Staff is null)
        {
            return;
        }

        if (_isOpen)
        {
            ReloadCurrentSection();
        }
        else
        {
            _reloadOnOpen = true;
        }
    }

    /// <summary>
    /// Re-derives which sections exist and keeps the open one legal. Called on every authority
    /// change, so a package revoked at 11:04 closes its section at 11:04.
    /// </summary>
    public void ApplyCapabilities(ShellCapabilities capabilities)
    {
        _capabilities = capabilities;

        VisibleSections.Clear();
        foreach (var section in Sections.Where(s => s.IsAllowed(capabilities)))
        {
            VisibleSections.Add(section);
        }

        if (Current is null || !Current.IsAllowed(capabilities))
        {
            SetCurrent(Staff is null ? null : PickDefault());
            ReloadCurrentSection();
        }
    }

    [RelayCommand]
    private void SelectSection(StaffSectionViewModel? section)
    {
        if (section is null || Staff is null || !section.IsAllowed(_capabilities) || section == Current)
        {
            return;
        }

        SetCurrent(section);
        ReloadCurrentSection();
    }

    /// <summary>The single copy of the fan-out that used to be a six-branch if-else in two shells.</summary>
    public void ReloadCurrentSection()
    {
        if (Staff is not { } staff || Current is not { } section)
        {
            return;
        }

        // Every load is scoped to this call, so picking another member or another section drops
        // the previous request instead of letting it resolve into the screen that replaced it.
        // Reloading here is also what a pending _reloadOnOpen was asking for, so it is satisfied.
        _reloadOnOpen = false;
        CancelSection();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _sectionCts = cts;
        var ct = cts.Token;
        var task = section.Id switch
        {
            StaffSectionId.Summary => Summary.LoadAsync(
                staff.UserId, staff.Name, force: true,
                PeriodFilter.AppliedFromUtc, PeriodFilter.AppliedToUtc,
                PeriodFilter.AppliedStart, PeriodFilter.AppliedEnd, ct),
            StaffSectionId.Screenshot => Screenshots.LoadAsync(
                staff.UserId, force: true,
                PeriodFilter.AppliedFromUtc, PeriodFilter.AppliedToUtc, ct),
            StaffSectionId.Browsing => Browsing.LoadAsync(
                staff.UserId, force: true,
                PeriodFilter.AppliedFromUtc, PeriodFilter.AppliedToUtc, ct),
            StaffSectionId.TimeTrack => TimeTrack.LoadAsync(
                staff.UserId, force: true,
                PeriodFilter.AppliedFromUtc, PeriodFilter.AppliedToUtc,
                PeriodFilter.AppliedStart, PeriodFilter.AppliedEnd, ct),
            StaffSectionId.AppHistory => AppHistory.LoadAsync(
                staff.UserId, force: true,
                PeriodFilter.AppliedFromUtc, PeriodFilter.AppliedToUtc, ct),
            _ => TrackingSettings.LoadAsync(staff.UserId, staff.Name, force: true, ct)
        };

        task.FireAndForget(_log, $"{section.Label} load");
    }

    /// <summary>
    /// Cancel before dispose: a token whose source is already cancelled still short-circuits every
    /// later registration, so the in-flight load unwinds cleanly rather than throwing at it.
    /// </summary>
    private void CancelSection()
    {
        var cts = _sectionCts;
        _sectionCts = null;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    private StaffSectionViewModel? PickDefault() => VisibleSections.FirstOrDefault();

    private void SetCurrent(StaffSectionViewModel? section)
    {
        foreach (var candidate in Sections)
        {
            candidate.IsActive = candidate == section;
        }

        Current = section;
    }

    partial void OnStaffChanged(StaffListItemViewModel? oldValue, StaffListItemViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnStaffPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnStaffPropertyChanged;
        }

        OnPropertyChanged(nameof(IsStaffSelected));
        OnPropertyChanged(nameof(ShowSectionNav));
        RaiseStaffFacets();
    }

    partial void OnCurrentChanged(StaffSectionViewModel? value) => OnPropertyChanged(nameof(CurrentContent));

    private void OnStaffPropertyChanged(object? sender, PropertyChangedEventArgs e) => RaiseStaffFacets();

    private void RaiseStaffFacets()
    {
        OnPropertyChanged(nameof(StaffName));
        OnPropertyChanged(nameof(StaffAvatar));
        OnPropertyChanged(nameof(StaffHasAvatar));
    }
}
