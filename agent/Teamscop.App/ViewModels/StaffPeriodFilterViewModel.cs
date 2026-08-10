using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Teamscop.App.Composition;
using Teamscop.App.Services;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.ViewModels;

public sealed partial class CalendarDayCellViewModel : ObservableObject
{
    public DateTime Date { get; init; }
    public int DayNumber => Date.Day;
    public Action<CalendarDayCellViewModel>? OnSelect { get; set; }

    [ObservableProperty] private bool _isCurrentMonth;
    [ObservableProperty] private bool _isToday;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isRangeStart;
    [ObservableProperty] private bool _isRangeEnd;
    [ObservableProperty] private bool _isInRange;
    /// <summary>True for every day in the selected period (start..end inclusive).</summary>
    [ObservableProperty] private bool _isInPeriod;
    [ObservableProperty] private bool _isDisabled;
    [ObservableProperty] private double _dayOpacity = 1;

    [RelayCommand]
    private void Click() => OnSelect?.Invoke(this);
}

/// <summary>
/// Pinned staff-detail period filter. Dates are company business-local calendar days.
/// </summary>
public sealed partial class StaffPeriodFilterViewModel : ObservableObject
{
    private readonly CompanyClock _clock;
    private DateTime? _rangeAnchor;
    private bool _selectingEnd;

    public StaffPeriodFilterViewModel(AppServices services)
    {
        _clock = services.Clock;
        var today = _clock.Today;
        VisibleYear = today.Year;
        VisibleMonth = today.Month;
        SelectedMonthOption = MonthOptions[VisibleMonth - 1];
        for (var y = today.Year - 6; y <= today.Year + 2; y++)
        {
            YearOptions.Add(y);
        }

        RebuildGrid();
    }

    public sealed record MonthOption(int Number, string Name)
    {
        public override string ToString() => Name;
    }

    public event Action? FilterChanged;

    public ObservableCollection<CalendarDayCellViewModel> Days { get; } = [];
    public ObservableCollection<int> YearOptions { get; } = [];

    public IReadOnlyList<string> WeekdayHeaders { get; } =
        ["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"];

    public IReadOnlyList<string> MonthNames { get; } =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December"
    ];

    public IReadOnlyList<MonthOption> MonthOptions { get; } =
    [
        new(1, "January"), new(2, "February"), new(3, "March"), new(4, "April"),
        new(5, "May"), new(6, "June"), new(7, "July"), new(8, "August"),
        new(9, "September"), new(10, "October"), new(11, "November"), new(12, "December")
    ];

    [ObservableProperty] private bool _isCalendarOpen;
    [ObservableProperty] private int _visibleYear;
    [ObservableProperty] private int _visibleMonth;
    [ObservableProperty] private MonthOption? _selectedMonthOption;
    [ObservableProperty] private DateTime? _selectedStart;
    [ObservableProperty] private DateTime? _selectedEnd;
    [ObservableProperty] private DateTime? _appliedStart;
    [ObservableProperty] private DateTime? _appliedEnd;
    [ObservableProperty] private string _rangeLabel = "All time";
    [ObservableProperty] private bool _hasActiveFilter;

    public string MonthTitle =>
        VisibleMonth is >= 1 and <= 12
            ? $"{MonthNames[VisibleMonth - 1]} {VisibleYear}"
            : $"{VisibleYear}";

    /// <summary>Inclusive start: selected day's 00:00 business-local, as UTC.</summary>
    public DateTimeOffset? AppliedFromUtc { get; private set; }

    /// <summary>Exclusive end: last day's 24:00 business-local (next midnight), as UTC.</summary>
    public DateTimeOffset? AppliedToUtc { get; private set; }

    partial void OnVisibleYearChanged(int value)
    {
        OnPropertyChanged(nameof(MonthTitle));
        RebuildGrid();
    }

    partial void OnVisibleMonthChanged(int value)
    {
        OnPropertyChanged(nameof(MonthTitle));
        if (value is >= 1 and <= 12
            && (SelectedMonthOption is null || SelectedMonthOption.Number != value))
        {
            SelectedMonthOption = MonthOptions[value - 1];
        }

        RebuildGrid();
    }

    partial void OnSelectedMonthOptionChanged(MonthOption? value)
    {
        if (value is not null && VisibleMonth != value.Number)
        {
            VisibleMonth = value.Number;
        }
    }

    partial void OnSelectedStartChanged(DateTime? value) => RefreshSelectionStyles();
    partial void OnSelectedEndChanged(DateTime? value) => RefreshSelectionStyles();

    /// <summary>
    /// Points the calendar at the company's today. Local only: the zone arrives once at start-up
    /// and again over SignalR, so re-reading /business-time every time a member or the calendar is
    /// opened was a round trip per click for a value the app already holds (§15.2).
    /// </summary>
    public void SyncToClock()
    {
        var local = _clock.Today;
        if (!YearOptions.Contains(local.Year))
        {
            YearOptions.Add(local.Year);
        }

        if (!IsCalendarOpen && AppliedStart is null)
        {
            VisibleYear = local.Year;
            VisibleMonth = local.Month;
        }
    }

    [RelayCommand]
    private void ToggleCalendar()
    {
        if (!IsCalendarOpen)
        {
            SyncToClock();
            SelectedStart = AppliedStart;
            SelectedEnd = AppliedEnd ?? AppliedStart;
            _selectingEnd = false;
            _rangeAnchor = null;
            if (SelectedStart is { } s)
            {
                VisibleYear = s.Year;
                VisibleMonth = s.Month;
            }

            RebuildGrid();
        }

        IsCalendarOpen = !IsCalendarOpen;
    }

    [RelayCommand]
    private void CloseCalendar() => IsCalendarOpen = false;

    [RelayCommand]
    private void PrevMonth()
    {
        var d = new DateTime(VisibleYear, VisibleMonth, 1).AddMonths(-1);
        VisibleYear = d.Year;
        VisibleMonth = d.Month;
    }

    [RelayCommand]
    private void NextMonth()
    {
        var d = new DateTime(VisibleYear, VisibleMonth, 1).AddMonths(1);
        VisibleYear = d.Year;
        VisibleMonth = d.Month;
    }

    [RelayCommand]
    private void GoToday()
    {
        var today = _clock.Today;
        VisibleYear = today.Year;
        VisibleMonth = today.Month;
        SelectedStart = today;
        SelectedEnd = today;
        _selectingEnd = false;
        _rangeAnchor = null;
        RebuildGrid();
    }

    [RelayCommand]
    private void SelectDay(CalendarDayCellViewModel? cell)
    {
        if (cell is null || cell.IsDisabled)
        {
            return;
        }

        var day = cell.Date.Date;

        // First click: single day (00:00–24:00 on Save). Second click: end of period.
        if (!_selectingEnd || _rangeAnchor is null)
        {
            SelectedStart = day;
            SelectedEnd = day;
            _rangeAnchor = day;
            _selectingEnd = true;
        }
        else if (day == _rangeAnchor.Value)
        {
            SelectedStart = day;
            SelectedEnd = day;
            _selectingEnd = false;
            _rangeAnchor = null;
        }
        else
        {
            if (day < _rangeAnchor.Value)
            {
                SelectedStart = day;
                SelectedEnd = _rangeAnchor;
            }
            else
            {
                SelectedStart = _rangeAnchor;
                SelectedEnd = day;
            }

            _selectingEnd = false;
            _rangeAnchor = null;
        }

        RefreshSelectionStyles();
    }

    [RelayCommand]
    private void SaveFilter()
    {
        if (SelectedStart is null)
        {
            ClearFilter();
            return;
        }

        ApplyRange(SelectedStart.Value, SelectedEnd ?? SelectedStart.Value);
    }

    /// <summary>
    /// Applies a period without going through the calendar — the leaderboard opens on the current
    /// week, and a staff member opens on today. Both days are company business-local (§8.2).
    /// </summary>
    public void ApplyRange(DateTime startDay, DateTime endDay) => ApplyRange(startDay, endDay, raise: true);

    /// <summary>
    /// §2.5 — a staff member opens on today's period so the timetrack bar and every section have a
    /// concrete span from the first frame instead of the empty "select a period" state. The applied
    /// range is seeded silently (the caller triggers the one reload), and a period already chosen is
    /// kept so re-selecting a member does not reset their calendar.
    /// </summary>
    public void EnsureDefaultPeriod()
    {
        SyncToClock();
        if (AppliedStart is not null)
        {
            return;
        }

        var today = _clock.Today;
        ApplyRange(today, today, raise: false);
    }

    private void ApplyRange(DateTime startDay, DateTime endDay, bool raise)
    {
        var start = startDay.Date;
        var end = endDay.Date;
        if (end < start)
        {
            (start, end) = (end, start);
        }

        SelectedStart = start;
        SelectedEnd = end;
        AppliedStart = start;
        AppliedEnd = end;
        // Day D → [D 00:00, D 24:00) in company business-local time.
        AppliedFromUtc = _clock.ToUtc(start);
        AppliedToUtc = _clock.ToUtc(end.AddDays(1));
        HasActiveFilter = true;
        RangeLabel = CompanyClock.FormatDayRange(start, end);
        IsCalendarOpen = false;
        _selectingEnd = false;
        _rangeAnchor = null;
        if (raise)
        {
            FilterChanged?.Invoke();
        }
    }

    /// <summary>
    /// §8.2 — the company zone moved, so the same calendar days now span different instants and
    /// "today" may be a different date. Recomputes the applied bounds and the grid in place; the
    /// caller decides what to reload, so this never raises <see cref="FilterChanged"/> itself.
    /// </summary>
    public void ReapplyClock()
    {
        var local = _clock.Today;
        if (!YearOptions.Contains(local.Year))
        {
            YearOptions.Add(local.Year);
        }

        if (AppliedStart is { } start && AppliedEnd is { } end)
        {
            AppliedFromUtc = _clock.ToUtc(start);
            AppliedToUtc = _clock.ToUtc(end.AddDays(1));
            RangeLabel = CompanyClock.FormatDayRange(start, end);
        }

        RebuildGrid();
    }

    [RelayCommand]
    private void ClearFilter()
    {
        SelectedStart = null;
        SelectedEnd = null;
        AppliedStart = null;
        AppliedEnd = null;
        AppliedFromUtc = null;
        AppliedToUtc = null;
        HasActiveFilter = false;
        RangeLabel = "All time";
        _selectingEnd = false;
        _rangeAnchor = null;
        RefreshSelectionStyles();
        IsCalendarOpen = false;
        FilterChanged?.Invoke();
    }

    private void RebuildGrid()
    {
        Days.Clear();
        if (VisibleMonth is < 1 or > 12)
        {
            return;
        }

        var first = new DateTime(VisibleYear, VisibleMonth, 1);
        // Monday-first
        var offset = ((int)first.DayOfWeek + 6) % 7;
        var cursor = first.AddDays(-offset);
        var today = _clock.Today;

        for (var i = 0; i < 42; i++)
        {
            var d = cursor.AddDays(i);
            var inMonth = d.Month == VisibleMonth;
            Days.Add(new CalendarDayCellViewModel
            {
                Date = d,
                IsCurrentMonth = inMonth,
                IsToday = d.Date == today,
                DayOpacity = inMonth ? 1.0 : 0.38,
                OnSelect = cell => SelectDayCommand.Execute(cell)
            });
        }

        RefreshSelectionStyles();
    }

    private void RefreshSelectionStyles()
    {
        var start = SelectedStart?.Date;
        var end = (SelectedEnd ?? SelectedStart)?.Date;
        if (start is not null && end is not null && end < start)
        {
            (start, end) = (end, start);
        }

        foreach (var cell in Days)
        {
            var d = cell.Date.Date;
            cell.IsRangeStart = start is not null && d == start;
            cell.IsRangeEnd = end is not null && d == end;
            cell.IsSelected = cell.IsRangeStart || cell.IsRangeEnd;
            cell.IsInRange = start is not null && end is not null && d > start && d < end;
            cell.IsInPeriod = start is not null && end is not null && d >= start && d <= end;
        }
    }
}
