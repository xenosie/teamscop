using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Teamscop.App.Composition;
using Teamscop.App.Services;
using Teamscop.App.Views;

namespace Teamscop.App;

public partial class App : Application
{
    /// <summary>Handed over by <see cref="Program"/> before the platform starts; null in tests.</summary>
    public static SingleInstanceGuard? Instance { get; set; }

    private TrayHost? _tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Zero I/O, zero blocking. One window is created and shown; who the user is and what they
    /// may see is resolved asynchronously inside it. The previous version blocked the UI thread
    /// on two sequential HTTPS calls, so an unreachable host meant a windowless frozen process.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = AppServices.Current;
            var shell = new ShellWindow();
            services.Sticker.Attach(shell);

            // Defect 2 — the branch is chosen by one rule, and that rule guarantees a way back.
            var plan = ShellPresencePlan.For(services.Sticker.IsActive);

            if (plan.StickerRestoresShell)
            {
                // A monitored machine gets its sticker here — before any request, never waiting
                // on one (§14.4). The shell stays HIDDEN until authorities prove this account has
                // a workspace (§4.4); ShellViewModel reveals it.
                //
                // MainWindow is deliberately not assigned: Avalonia shows it automatically, which
                // flashed the full workspace onto every monitored employee's screen on every boot
                // and left it there whenever the machine was offline.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                services.Sticker.Show();

                // §14.2 — this process is the second reporting channel. It runs on a monitored
                // machine, in the user's session, independently of the service, so when the service
                // goes silent the server still hears from here and can tell a stopped service apart
                // from a switched-off PC. Started only on the monitored branch: an admin console has
                // no status to report.
                services.StatusReporter.Start();
            }
            else
            {
                // Admin console, or a machine with no session yet: the shell is the whole UI, and
                // closing it genuinely exits so the shortcut is a real way back in.
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.MainWindow = shell;
            }

            if (plan.ShowTrayIcon)
            {
                _tray = new TrayHost(services.Log);
                _tray.Install(
                    open: () => Dispatcher.UIThread.Post(services.Sticker.ShowShell),
                    exit: () => Dispatcher.UIThread.Post(() => desktop.Shutdown()));
            }

            // A second launch (shortcut, Start Menu, autostart) raises the window that already
            // exists instead of starting a shell nobody asked for.
            if (Instance is { } guard)
            {
                guard.ActivationRequested += () => Dispatcher.UIThread.Post(services.Sticker.ShowShell);
            }

            desktop.ShutdownRequested += (_, _) => _tray?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
