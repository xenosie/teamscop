using CommunityToolkit.Mvvm.ComponentModel;

namespace Teamscop.App.ViewModels;

/// <summary>
/// Signed in, but holding no packages and leading no team. A route inside the shell rather than
/// its own window, so a promotion turns it into a workspace without a restart.
/// </summary>
public sealed partial class NoAccessViewModel : ObservableObject
{
    [ObservableProperty] private string _userLine = string.Empty;
}
