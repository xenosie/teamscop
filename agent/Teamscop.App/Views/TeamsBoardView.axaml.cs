using Avalonia.Controls;
using Avalonia.Interactivity;
using Teamscop.App.ViewModels;

namespace Teamscop.App.Views;

public partial class TeamsBoardView : UserControl
{
    public TeamsBoardView()
    {
        InitializeComponent();
    }

    private TeamsBoardViewModel? Vm => DataContext as TeamsBoardViewModel;

    private async void OnTeamNameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not TextBox { Tag: TeamBoxViewModel team })
        {
            return;
        }

        await Vm.RenameTeamCommand.ExecuteAsync(team);
    }

    private async void OnDeleteTeamClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not Button { Tag: TeamBoxViewModel team })
        {
            return;
        }

        await Vm.DeleteTeamCommand.ExecuteAsync(team);
    }

    private async void OnAddLeaderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not Button { Tag: TeamBoxViewModel team })
        {
            return;
        }

        await Vm.AddLeaderCommand.ExecuteAsync(team);
    }

    private async void OnSwitchLeaderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not Button { Tag: TeamBoxViewModel team })
        {
            return;
        }

        await Vm.SwitchLeaderCommand.ExecuteAsync(team);
    }

    private async void OnAddMemberClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not Button { Tag: TeamBoxViewModel team })
        {
            return;
        }

        await Vm.AddMemberCommand.ExecuteAsync(team);
    }

    private async void OnRemovePersonClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not Button { Tag: StaffCardViewModel person })
        {
            return;
        }

        await Vm.RemovePersonCommand.ExecuteAsync(person);
    }
}
