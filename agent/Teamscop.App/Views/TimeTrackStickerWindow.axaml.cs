using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Teamscop.App.Services;
using Teamscop.App.ViewModels;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.Views;

public partial class TimeTrackStickerWindow : Window
{
    private readonly TimeTrackStickerViewModel _vm;
    private bool _rebuildQueued;
    private bool _suppressPositionSave;
    private DispatcherTimer? _positionSaveTimer;

    public TimeTrackStickerWindow()
        : this(AppSessionStore.ForActiveSession().Load())
    {
    }

    public TimeTrackStickerWindow(LocalAgentState state)
    {
        InitializeComponent();
        _vm = new TimeTrackStickerViewModel(state);
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Segments.CollectionChanged += OnSegmentsChanged;

        Opened += OnOpened;
        StaffShellLifetime.AttachStickerStayAlive(this);
        PositionChanged += (_, _) => QueuePersistPosition();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        RestorePosition();
        await _vm.InitializeAsync();
        QueueRebuild();
    }

    private void OnDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginMoveDrag(e);
    }

    private void RestorePosition()
    {
        var saved = TimeTrackStickerPositionStore.Load();
        if (saved is null)
        {
            // Default: bottom-right-ish of primary screen.
            if (Screens.Primary is { } screen)
            {
                var area = screen.WorkingArea;
                _suppressPositionSave = true;
                Position = new PixelPoint(
                    Math.Max(area.X, area.X + area.Width - (int)Width - 24),
                    Math.Max(area.Y, area.Y + area.Height - (int)Height - 48));
                _suppressPositionSave = false;
            }

            return;
        }

        _suppressPositionSave = true;
        Position = new PixelPoint((int)Math.Round(saved.X), (int)Math.Round(saved.Y));
        _suppressPositionSave = false;
    }

    private void QueuePersistPosition()
    {
        if (_suppressPositionSave)
        {
            return;
        }

        _positionSaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _positionSaveTimer.Tick -= OnPositionSaveTick;
        _positionSaveTimer.Tick += OnPositionSaveTick;
        _positionSaveTimer.Stop();
        _positionSaveTimer.Start();
    }

    private void OnPositionSaveTick(object? sender, EventArgs e)
    {
        _positionSaveTimer?.Stop();
        PersistPosition();
    }

    private void PersistPosition()
        => TimeTrackStickerPositionStore.Save(Position.X, Position.Y);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TimeTrackStickerViewModel.HasTimeline))
        {
            QueueRebuild();
        }
    }

    private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => QueueRebuild();

    private void QueueRebuild()
    {
        if (_rebuildQueued)
        {
            return;
        }

        _rebuildQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rebuildQueued = false;
            RebuildBar();
        }, DispatcherPriority.Render);
    }

    private void RebuildBar()
    {
        TimelineBar.Children.Clear();
        TimelineBar.ColumnDefinitions.Clear();
        if (_vm.Segments.Count == 0)
        {
            return;
        }

        var i = 0;
        foreach (var seg in _vm.Segments)
        {
            var weight = Math.Max(seg.DurationSeconds, 0.001);
            TimelineBar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(weight, GridUnitType.Star)));

            var cell = new Border
            {
                Background = seg.Brush,
                BorderThickness = new Thickness(0)
            };
            Grid.SetColumn(cell, i);
            TimelineBar.Children.Add(cell);
            i++;
        }
    }
}
