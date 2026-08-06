using Avalonia.Controls;
using Teamscop.App.Services;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.Views;

public partial class NoAccessWorkspaceWindow : Window
{
    public NoAccessWorkspaceWindow()
        : this(AppSessionStore.ForActiveSession().Load())
    {
    }

    public NoAccessWorkspaceWindow(LocalAgentState state)
    {
        InitializeComponent();
        UserLabel.Text = string.IsNullOrWhiteSpace(state.Username)
            ? state.CompanyName ?? ""
            : $"{state.Username} · {state.CompanyName}";
    }
}
