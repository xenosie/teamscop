using Avalonia;
using System;
using Teamscop.App.Composition;
using Teamscop.App.Services;

namespace Teamscop.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Composition root first: one HttpClient, one session, one log — built before any window.
        var services = AppServices.Initialize();

        // Defect 2 — a second launch must raise the shell that is already up, not add another one
        // (and certainly not add one nobody can tell apart). The guard fails open, so a machine
        // where the OS refuses the primitives still starts.
        var guard = SingleInstanceGuard.Acquire(SingleInstanceGuard.DefaultName, services.Log);
        if (!guard.IsPrimary)
        {
            var raised = guard.SignalPrimary();
            services.Log.Info(raised
                ? "Teamscop is already running — raising the existing window"
                : "Teamscop is already running");
            guard.Dispose();
            services.Dispose();
            return;
        }

        guard.ListenForActivation(services.Log);
        App.Instance = guard;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            guard.Dispose();
            services.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new X11PlatformOptions
            {
                RenderingMode =
                [
                    X11RenderingMode.Software,
                    X11RenderingMode.Egl,
                    X11RenderingMode.Glx
                ]
            })
            .LogToTrace();
}
