using Avalonia.Controls;
using Teamscop.App.Services;
using Teamscop.App.ViewModels;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.Views;

public partial class PoliceWorkspaceWindow : Window
{
    private readonly WorkspaceViewModel _vm;

    public PoliceWorkspaceWindow()
        : this(AppSessionStore.ForActiveSession().Load())
    {
    }

    public PoliceWorkspaceWindow(LocalAgentState state)
    {
        InitializeComponent();
        _vm = new WorkspaceViewModel(state, RoleShellKind.Police);
        DataContext = _vm;
        WorkspaceHost.DataContext = _vm;
        _vm.TeamsBoard.RequestPickMember = (title, candidates) =>
            AddMemberDialog.PickAsync(this, title, candidates);
        _vm.Browsing.RequestOpenDomainDetail = (domain, staffId, from, to) =>
            _ = BrowsingDomainDetailWindow.ShowAsync(this, domain, staffId, from, to);
        Opened += async (_, _) => await _vm.InitializeAsync();
        StaffShellLifetime.AttachCloseToSticker(this, state, () => _vm.DisposeAsync().AsTask());
    }
}
