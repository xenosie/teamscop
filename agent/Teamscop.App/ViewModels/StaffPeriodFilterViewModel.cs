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
    private readonly LocalAgentStore _store;
    private string _apiBaseUrl;
    private BusinessClockConfig _clockConfig = new();
    private DateTime? _rangeAnchor;
    private bool _selectingEnd;

    public StaffPeriodFilterViewModel(string? apiBaseUrl = null)
    {
        _store = AppSessionStore.ForActiveSession();
        _apiBaseUrl = ResolveApiBase(apiBaseUrl);
        var today = DateTime.UtcNow.Date;
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

    public async Task EnsureClockAsync()
    {
        var state = _store.Load();
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            return;
        }

        _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
        try
        {
            using var api = new BusinessTimeApiClient(_apiBaseUrl);
            _clockConfig = await api.GetMineAsync(state.AccessToken).ConfigureAwait(false);
            var bizToday = new BusinessClock();
            bizToday.Apply(_clockConfig);
            var local = bizToday.Now().BusinessLocal.Date;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!YearOptions.Contains(local.Year))
                {
                    YearOptions.Add(local.Year);
                }

                if (!IsCalendarOpen && AppliedStart is null)
                {
                    VisibleYear = local.Year;
                    VisibleMonth = local.Month;
                }
            });
        }
        catch
        {
            // keep previous / UTC defaults
        }
    }

    [RelayCommand]
    private async Task ToggleCalendarAsync()
    {
        if (!IsCalendarOpen)
        {
            await EnsureClockAsync();
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
        var clock = new BusinessClock();
        clock.Apply(_clockConfig);
        var today = clock.Now().BusinessLocal.Date;
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

        var start = SelectedStart.Value.Date;
        var end = (SelectedEnd ?? SelectedStart.Value).Date;
        if (end < start)
        {
            (start, end) = (end, start);
        }

        AppliedStart = start;
        AppliedEnd = end;
        // Day D → [D 00:00, D 24:00) in company business-local time.
        AppliedFromUtc = BusinessClock.BusinessLocalToUtc(_clockConfig, start);
        AppliedToUtc = BusinessClock.BusinessLocalToUtc(_clockConfig, end.AddDays(1));
        HasActiveFilter = true;
        RangeLabel = start == end
            ? start.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
            : $"{start:dd MMM yyyy} – {end:dd MMM yyyy}";
        IsCalendarOpen = false;
        FilterChanged?.Invoke();
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
        var today = DateTime.UtcNow.Date;
        try
        {
            var clock = new BusinessClock();
            clock.Apply(_clockConfig);
            today = clock.Now().BusinessLocal.Date;
        }
        catch
        {
            // keep UTC date
        }

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

    private static string ResolveApiBase(string? apiBaseUrl)
        => string.IsNullOrWhiteSpace(apiBaseUrl)
            ? Environment.GetEnvironmentVariable("TEAMSCOP_API_BASE") ?? "https://teamscop.com"
            : apiBaseUrl.TrimEnd('/');
}
