using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Teamscop.App.ViewModels;

namespace Teamscop.App.Views;

public partial class AuthWindow : Window
{
    private readonly AuthViewModel _vm;
    private static readonly IBrush SolidBlue = Brush.Parse("#2563EB");
    private static readonly IBrush IdleFg = Brush.Parse("#0F172A");

    public AuthWindow()
    {
        InitializeComponent();

        Width = 960;
        Height = 640;
        MinWidth = 720;
        MinHeight = 520;
        ClientSize = new Size(960, 640);
        WindowState = WindowState.Normal;
        ShowActivated = true;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(320, 80);

        _vm = new AuthViewModel();
        DataContext = _vm;
        _vm.RequestPhotoCrop = () => PhotoCropWindow.PickAndCropAsync(this);
        _vm.Authenticated += OnAuthenticated;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AuthViewModel.Mode)
                or nameof(AuthViewModel.IsCreateCompany)
                or nameof(AuthViewModel.IsJoinCompany))
            {
                RefreshModeChrome();
            }
        };
        Opened += (_, _) => RefreshModeChrome();
    }

    private void RefreshModeChrome()
    {
        Highlight(BtnRegisterBusiness, _vm.IsCreateCompany);
        Highlight(BtnJoinBusiness, _vm.IsJoinCompany);
    }

    private static void Highlight(Button button, bool active)
    {
        if (active)
        {
            button.Background = SolidBlue;
            button.Foreground = Brushes.White;
            button.BorderBrush = SolidBlue;
        }
        else
        {
            button.Background = Brushes.Transparent;
            button.Foreground = IdleFg;
            button.BorderBrush = SolidBlue;
        }
    }

    private void OnAuthenticated(Engine.Auth.AuthSession session)
    {
        if (!string.Equals(session.User.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Prefer freshly persisted admin state (includes company name).
            var state = new Engine.Lifecycle.LocalAgentStore(Engine.Lifecycle.AgentRole.Admin).Load();
            if (string.IsNullOrWhiteSpace(state.AccessToken))
            {
                state.AccessToken = session.AccessToken;
                state.DeviceKey = session.User.DeviceKey;
                state.Role = session.User.Role;
                state.CompanyId = session.User.Company.Id;
                state.Username = session.User.Username;
                state.CompanyName = session.User.Company.Name;
                state.CompanyAvatarUrl = session.User.Company.AvatarUrl;
            }

            var main = new MainWindow(state);
            desktop.MainWindow = main;
            main.Show();
            Close();
        }
    }
}
