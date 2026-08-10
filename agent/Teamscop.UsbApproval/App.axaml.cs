using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Teamscop.Engine.Usb;
using Teamscop.UsbApproval.Views;

namespace Teamscop.UsbApproval;

public partial class App : Application
{
    /// <summary>
    /// Handed over by Main before Avalonia starts. The window MUST be constructed here — the
    /// StartWithClassicDesktopLifetime callback runs before the windowing platform is initialised,
    /// so a Window created there throws, the catch in Main swallowed it, and this sticker had
    /// never appeared: every USB approval failed before the user could enter a code.
    /// </summary>
    public static UsbApprovalRequest? Request { get; set; }

    public static string? ResponsePath { get; set; }

    /// <summary>Set by the window on close; read by Main after the lifetime returns.</summary>
    public static int ResultExitCode { get; set; } = 2;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && Request is not null
            && !string.IsNullOrWhiteSpace(ResponsePath))
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            var window = new UsbStickerWindow(Request, ResponsePath!);
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
