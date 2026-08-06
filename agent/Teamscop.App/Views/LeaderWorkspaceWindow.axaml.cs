using Avalonia.Controls;
using Teamscop.App.Services;
using Teamscop.App.ViewModels;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.Views;

public partial class LeaderWorkspaceWindow : Window
{
    private readonly WorkspaceViewModel _vm;

    public LeaderWorkspaceWindow()
        : this(AppSessionStore.ForActiveSession().Load())
    {
    }

    public LeaderWorkspaceWindow(LocalAgentState state)
    {
        InitializeComponent();
        _vm = new WorkspaceViewModel(state, RoleShellKind.Leader);
        DataContext = _vm;
        WorkspaceHost.DataContext = _vm;
        _vm.Browsing.RequestOpenDomainDetail = (domain, staffId, from, to) =>
            _ = BrowsingDomainDetailWindow.ShowAsync(this, domain, staffId, from, to);
        Opened += async (_, _) => await _vm.InitializeAsync();
        StaffShellLifetime.AttachCloseToSticker(this, state, () => _vm.DisposeAsync().AsTask());
    }
}
