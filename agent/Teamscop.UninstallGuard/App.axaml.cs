using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Teamscop.Engine.Lifecycle;
using Teamscop.UninstallGuard.Views;

namespace Teamscop.UninstallGuard;

public partial class App : Application
{
    /// <summary>
    /// Handed over by Main before Avalonia starts. Static because the window MUST be constructed
    /// here, in OnFrameworkInitializationCompleted — the StartWithClassicDesktopLifetime callback
    /// runs BEFORE the windowing platform is initialised, so a Window created there throws
    /// "Unable to locate IWindowingPlatform", the catch in Main swallowed it, and the process
    /// exited 1. The uninstaller read 1 as "refused", which meant the code-entry window had never
    /// once appeared on any machine: every uninstall failed with "authorization failed" before the
    /// user could type anything.
    /// </summary>
    public static IApprovalCodeVerifier? Verifier { get; set; }

    /// <summary>Set by the window on close; read by Main after the lifetime returns.</summary>
    public static int ResultExitCode { get; set; } = 2;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            var window = new UninstallStickerWindow(Verifier, auditAsync: null);
            desktop.MainWindow = window;
            window.Closed += (_, _) =>
            {
                ResultExitCode = window.ResultExitCode;
                desktop.Shutdown(ResultExitCode);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
