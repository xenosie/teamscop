using Avalonia;
using Avalonia.Controls;
using Teamscop.App.ViewModels;

namespace Teamscop.App.Views;

public partial class LeaderboardView : UserControl
{
    public LeaderboardView()
    {
        InitializeComponent();
    }

    /// <summary>An avatar is fetched when its row reaches the tree, never for the whole company.</summary>
    private void OnRowAttached(object? sender, VisualTreeAttachmentEventArgs e) => EnsureRow(sender);

    /// <summary>
    /// The list virtualises, and a virtualising panel recycles a container by handing it a new
    /// item rather than re-attaching it — so realisation alone would miss every row after the
    /// first screenful. <see cref="LeaderboardViewModel.EnsureAvatar"/> is idempotent per row.
    /// </summary>
    private void OnRowDataContextChanged(object? sender, EventArgs e) => EnsureRow(sender);

    private void EnsureRow(object? sender)
    {
        if (sender is Control { DataContext: LeaderboardRowViewModel row }
            && DataContext is LeaderboardViewModel vm)
        {
            vm.EnsureAvatar(row);
        }
    }
}
