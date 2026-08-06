using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Teamscop.App.Views;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.Services;

/// <summary>
/// Staff UI must stay live: workspace close → timetrack sticker; sticker cannot quit the process.
/// </summary>
public static class StaffShellLifetime
{
    public static void EnsureStaffShutdownMode()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }
    }

    public static void EnsureAdminShutdownMode()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
    }

    /// <summary>
    /// Wire Closing on a leader/police/officer workspace so Close shows the sticker instead of exiting.
    /// </summary>
    public static void AttachCloseToSticker(Window workspace, LocalAgentState state, Func<Task>? disposeAsync = null)
    {
        EnsureStaffShutdownMode();
        var allowClose = false;
        workspace.Closing += (_, e) =>
        {
            if (allowClose)
            {
                return;
            }

            e.Cancel = true;
            _ = TransitionAsync();
        };

        async Task TransitionAsync()
        {
            try
            {
                if (disposeAsync is not null)
                {
                    await disposeAsync().ConfigureAwait(true);
                }

                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                {
                    return;
                }

                var sticker = new TimeTrackStickerWindow(state);
                desktop.MainWindow = sticker;
                sticker.Show();
                allowClose = true;
                await Dispatcher.UIThread.InvokeAsync(workspace.Close);
            }
            catch
            {
                allowClose = false;
            }
        }
    }

    public static void AttachStickerStayAlive(Window sticker)
    {
        EnsureStaffShutdownMode();
        sticker.Closing += (_, e) =>
        {
            // Plain staff / post-close leaders: UI must remain live.
            e.Cancel = true;
        };
    }
}
