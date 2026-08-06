using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.ViewModels;

public sealed partial class PackageToggleViewModel : ObservableObject
{
    public PackageToggleViewModel(string id, string label, bool isEnabled)
    {
        Id = id;
        Label = label;
        IsEnabled = isEnabled;
    }

    public string Id { get; }
    public string Label { get; }

    [ObservableProperty] private bool _isEnabled;
}

public sealed partial class PolicemanRowViewModel : ObservableObject
{
    public PolicemanRowViewModel(
        Guid staffUserId,
        string username,
        IReadOnlyList<AuthorityPackageInfo> catalog,
        IReadOnlyList<string> granted)
    {
        StaffUserId = staffUserId;
        Username = username;
        var grantedSet = new HashSet<string>(granted, StringComparer.Ordinal);
        foreach (var pkg in catalog)
        {
            Packages.Add(new PackageToggleViewModel(
                pkg.Id,
                string.IsNullOrWhiteSpace(pkg.Label) ? pkg.Id : pkg.Label,
                grantedSet.Contains(pkg.Id)));
        }
    }

    public Guid StaffUserId { get; }
    public string Username { get; }
    public ObservableCollection<PackageToggleViewModel> Packages { get; } = [];

    public IReadOnlyList<string> SelectedPackageIds()
        => Packages.Where(p => p.IsEnabled).Select(p => p.Id).ToList();
}
