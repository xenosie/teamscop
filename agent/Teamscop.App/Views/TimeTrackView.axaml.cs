using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using Teamscop.App.ViewModels;

namespace Teamscop.App.Views;

public partial class TimeTrackView : UserControl
{
    private TimeTrackViewModel? _vm;
    private bool _rebuildQueued;

    public TimeTrackView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as TimeTrackViewModel);
        DetachedFromVisualTree += (_, _) => Attach(null);
    }

    private void Attach(TimeTrackViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm))
        {
            return;
        }

        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.Segments.CollectionChanged -= OnCollectionChanged;
        }

        _vm = vm;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.Segments.CollectionChanged += OnCollectionChanged;
            QueueRebuild();
        }
        else
        {
            ClearBar();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TimeTrackViewModel.HasTimeline)
            or nameof(TimeTrackViewModel.IsLoading)
            or nameof(TimeTrackViewModel.HasNowMarker)
            or nameof(TimeTrackViewModel.NowFraction))
        {
            QueueRebuild();
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => QueueRebuild();

    private void OnBarSizeChanged(object? sender, SizeChangedEventArgs e)
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

    private void ClearBar()
    {
        TimelineBar.Children.Clear();
        TimelineBar.ColumnDefinitions.Clear();
        NowOverlay.Children.Clear();
    }

    private void RebuildBar()
    {
        ClearBar();
        if (_vm is null || !_vm.HasTimeline || _vm.Segments.Count == 0)
        {
            return;
        }

        var i = 0;
        foreach (var seg in _vm.Segments)
        {
            var weight = Math.Max(seg.DurationSeconds, 0.001);
            TimelineBar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(weight, GridUnitType.Star)));

            var cell = new Border { Background = seg.Fill };
            ToolTip.SetTip(cell, seg.ToolTip);
            Grid.SetColumn(cell, i);
            TimelineBar.Children.Add(cell);
            i++;
        }

        DrawNowMarker();
    }

    /// <summary>
    /// §2.5 — the "now" rule, positioned by the fraction the ViewModel derived from the server's
    /// period bounds. Drawn over the segments, in the same frame, so it can never disagree with them.
    /// </summary>
    private void DrawNowMarker()
    {
        if (_vm is null || !_vm.HasNowMarker)
        {
            return;
        }

        var width = NowOverlay.Bounds.Width;
        var height = NowOverlay.Bounds.Height;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        var x = Math.Clamp(_vm.NowFraction * width, 0, width);

        var line = new Rectangle { Width = 2, Height = height, Fill = NowBrush };
        Canvas.SetLeft(line, Math.Clamp(x - 1, 0, Math.Max(0, width - 2)));
        Canvas.SetTop(line, 0);
        NowOverlay.Children.Add(line);

        var cap = new Ellipse { Width = 9, Height = 9, Fill = NowBrush };
        Canvas.SetLeft(cap, Math.Clamp(x - 4.5, 0, Math.Max(0, width - 9)));
        Canvas.SetTop(cap, -1);
        NowOverlay.Children.Add(cap);
    }

    private static readonly IBrush NowBrush = SolidColorBrush.Parse("#0F172A");
}
