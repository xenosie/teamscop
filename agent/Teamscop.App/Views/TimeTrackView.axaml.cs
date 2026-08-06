using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
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
            _vm.Segments.CollectionChanged -= OnSegmentsChanged;
        }

        _vm = vm;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.Segments.CollectionChanged += OnSegmentsChanged;
            QueueRebuild();
        }
        else
        {
            TimelineBar.Children.Clear();
            TimelineBar.ColumnDefinitions.Clear();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TimeTrackViewModel.HasTimeline)
            or nameof(TimeTrackViewModel.IsLoading))
        {
            QueueRebuild();
        }
    }

    private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
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

    private void RebuildBar()
    {
        TimelineBar.Children.Clear();
        TimelineBar.ColumnDefinitions.Clear();
        if (_vm is null || !_vm.HasTimeline || _vm.Segments.Count == 0)
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
            ToolTip.SetTip(cell, seg.ToolTip);
            Grid.SetColumn(cell, i);
            TimelineBar.Children.Add(cell);
            i++;
        }
    }
}
