using Avalonia.Controls;
using Teamscop.App.Composition;
using Teamscop.App.ViewModels;
using Avalonia.Threading;

namespace Teamscop.App.Views;

/// <summary>
/// The one window (§4.7). It is the composition edge: the only place that reaches for
/// <see cref="AppServices.Current"/> and the only place that hands ViewModels a way to open a
/// dialog. Everything else about what it shows lives in <see cref="ShellViewModel"/>.
/// </summary>
public partial class ShellWindow : Window
{
    private readonly ShellViewModel _vm;
    private readonly CancellationTokenSource _lifetime = new();

    public ShellWindow()
    {
        InitializeComponent();
        _vm = new ShellViewModel(AppServices.Current);
        DataContext = _vm;

        _vm.Teams.RequestPickMember = (title, candidates) => AddMemberDialog.PickAsync(this, title, candidates);
        _vm.Settings.RequestPickStaff = (title, candidates) => AddMemberDialog.PickAsync(this, title, candidates);
        _vm.Enroll.RequestPhotoCrop = () => PhotoCropWindow.PickAndCropAsync(this);
        _vm.StaffDetail.Browsing.RequestOpenDomainDetail = (domain, staffId, from, to) =>
            BrowsingDomainDetailWindow.ShowAsync(this, domain, staffId, from, to)
                .FireAndForget(AppServices.Current.Log, "Browsing detail window");

        Closed += OnClosed;

        // Not tied to Opened: on a monitored machine this window starts hidden and only appears
        // once authorities show the account has a workspace. Waiting for Opened would deadlock
        // that — the window can never open because the resolution that would open it never runs.
        //
        // The Post body is guarded rather than left as a bare lambda: a throw inside a dispatcher
        // callback is unhandled, and start-up failing silently would kill the process before the
        // window ever painted.
        Dispatcher.UIThread.Post(StartAsync, DispatcherPriority.Background);
    }

    private void StartAsync()
    {
        try
        {
            _vm.InitializeAsync(_lifetime.Token).FireAndForget(AppServices.Current.Log, "Shell start-up");
        }
        catch (Exception ex)
        {
            AppServices.Current.Log.Warn("Shell start-up could not begin", ex);
        }
    }

    /// <summary>
    /// In-flight loads stop here rather than resolving into a window that is gone. Async void is
    /// unavoidable on a lifetime event, so nothing inside it is allowed to escape.
    /// </summary>
    private async void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            await _lifetime.CancelAsync();
            await _vm.DisposeAsync();
            _lifetime.Dispose();
        }
        catch (Exception ex)
        {
            AppServices.Current.Log.Warn("Shell shutdown did not complete cleanly", ex);
        }
    }
}
