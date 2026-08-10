using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Teamscop.Setup.Ui;

public partial class InstallerApp : Application
{
    /// <summary>
    /// The install work, handed over by Main before Avalonia starts. Runs on a background thread;
    /// reports through the window. Static handoff because the window MUST be constructed in
    /// OnFrameworkInitializationCompleted — the lifetime callback runs before the windowing
    /// platform exists, the exact bug that kept the uninstall code window invisible for weeks.
    /// </summary>
    public static Func<IProgress<InstallProgress>, int>? Work { get; set; }

    public static string VersionLabel { get; set; } = "";

    /// <summary>
    /// Runs when the user clicks Finish after a successful install — this is what launches the app.
    /// Nothing proceeds until that click: the person closes the install on their own terms, and the
    /// first Teamscop window appears because they said so, not mid-way through reading the screen.
    /// </summary>
    public static Action? OnFinished { get; set; }

    /// <summary>The install's exit code, read by Main after the lifetime returns.</summary>
    public static int ResultExitCode { get; set; } = 1;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Work is not null)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var window = new InstallerWindow(Work, VersionLabel);
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

/// <summary>One progress tick from the install pipeline.</summary>
/// <param name="StepIndex">0-based index into <see cref="InstallSteps.Titles"/>.</param>
/// <param name="Percent">Overall completion, 0–100.</param>
/// <param name="Detail">One line of what is happening right now, e.g. a file name.</param>
public readonly record struct InstallProgress(int StepIndex, double Percent, string Detail);

/// <summary>The visible plan. Order matches the pipeline in Program.RunInstallCore.</summary>
public static class InstallSteps
{
    public static readonly string[] Titles =
    [
        "Preparing",
        "Removing old versions",
        "Stopping services",
        "Copying files",
        "Configuring",
        "Installing service",
        "Finishing"
    ];
}
