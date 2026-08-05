using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Teamscop.App.Services;
using Teamscop.App.Views;

namespace Teamscop.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Registered admin machine → dashboard. Otherwise → auth.
            desktop.MainWindow = AdminSessionGate.TryLoadRegisteredAdmin(out var state, out _)
                ? new MainWindow(state)
                : new AuthWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
