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
            if (AdminSessionGate.TryLoadRegisteredSession(out var state, out _))
            {
                AppSessionStore.SetActive(state.Role);
                if (AppSessionStore.IsAdminRole(state.Role))
                {
                    StaffShellLifetime.EnsureAdminShutdownMode();
                }
                else
                {
                    StaffShellLifetime.EnsureStaffShutdownMode();
                }

                // Admin → MainWindow; leader/police/officer → role workspace; plain staff → sticker.
                desktop.MainWindow = RoleShellRouter.CreateWindowAsync(state).GetAwaiter().GetResult();
            }
            else
            {
                desktop.MainWindow = new AuthWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
