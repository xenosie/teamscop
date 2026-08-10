using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Teamscop.App.Composition;
using Teamscop.App.ViewModels;

namespace Teamscop.App.Views;

public partial class BrowsingDomainDetailWindow : Window
{
    public BrowsingDomainDetailWindow()
    {
        InitializeComponent();
    }

    public static async Task ShowAsync(
        Window owner,
        string domain,
        Guid staffUserId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
                await ShowCoreAsync(owner, domain, staffUserId, fromUtc, toUtc).ConfigureAwait(true));
            return;
        }

        await ShowCoreAsync(owner, domain, staffUserId, fromUtc, toUtc);
    }

    private static async Task ShowCoreAsync(
        Window owner,
        string domain,
        Guid staffUserId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc)
    {
        var vm = new BrowsingDomainDetailViewModel(
            AppServices.Current, domain, staffUserId, fromUtc, toUtc);
        var win = new BrowsingDomainDetailWindow { DataContext = vm };
        vm.LoadAsync().FireAndForget(AppServices.Current.Log, "Browsing detail");
        await win.ShowDialog(owner);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
