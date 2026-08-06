using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Teamscop.App.ViewModels;

namespace Teamscop.App.Views;

public partial class AddMemberDialog : Window
{
    public Guid? SelectedUserId { get; private set; }

    public AddMemberDialog()
    {
        InitializeComponent();
        BtnCancel.Click += (_, _) => Close(null);
    }

    public void SetCandidates(string title, IReadOnlyList<StaffCardViewModel> candidates)
    {
        Title = title;
        TitleBar.Title = title;
        StaffList.ItemsSource = candidates;
        EmptyText.IsVisible = candidates.Count == 0;
    }

    private void OnPickClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: StaffCardViewModel staff })
        {
            SelectedUserId = staff.UserId;
            Close(staff.UserId);
        }
    }

    public static async Task<Guid?> PickAsync(
        Window owner, string title, IReadOnlyList<StaffCardViewModel> candidates)
    {
        // Windows must be created on the UI thread — callers may resume after ConfigureAwait(false).
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
                await PickCoreAsync(owner, title, candidates).ConfigureAwait(true));
        }

        return await PickCoreAsync(owner, title, candidates);
    }

    private static async Task<Guid?> PickCoreAsync(
        Window owner, string title, IReadOnlyList<StaffCardViewModel> candidates)
    {
        var dlg = new AddMemberDialog();
        dlg.SetCandidates(title, candidates);
        return await dlg.ShowDialog<Guid?>(owner);
    }
}
