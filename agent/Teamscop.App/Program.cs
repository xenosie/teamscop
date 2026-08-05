using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using System;

namespace Teamscop.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

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
