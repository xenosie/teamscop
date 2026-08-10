using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HeroIconsAvalonia.Enums;

namespace Teamscop.App.ViewModels;

/// <summary>
/// Top-level destinations of the single adaptive shell (§4.7). Which of these exist for a given
/// user is derived from <see cref="ShellCapabilities"/> every time authorities change — never
/// baked in at start-up.
/// </summary>
public enum ShellRouteId
{
    /// <summary>§14.2 ranking by hours worked — the landing route (§1.3 deleted Today).</summary>
    Leaderboard,
    Staff,
    Teams,
    Codes,
    Settings,
    NoAccess
}

/// <summary>
/// One nav entry. <see cref="IsActive"/> is a binding (<c>Classes.selected</c> plus
/// <see cref="IconBrush"/>), which is what removes both imperative RefreshSidebar() passes and
/// the PropertyChanged name-matching blocks that drove them.
/// </summary>
public sealed partial class ShellRouteViewModel : ObservableObject
{
    private static readonly IBrush AccentIcon = SolidColorBrush.Parse("#2563EB");

    public ShellRouteViewModel(ShellRouteId id, string label, IconType icon, Func<object?> content)
    {
        Id = id;
        _label = label;
        Icon = icon;
        Content = content;
    }

    public ShellRouteId Id { get; }
    public IconType Icon { get; }

    /// <summary>Observable because the Staff route reads "My team" for a leader (§4.7).</summary>
    [ObservableProperty] private string _label;

    /// <summary>Resolved late so a route can hand back a VM the shell built after the route table.</summary>
    public Func<object?> Content { get; }

    [ObservableProperty] private bool _isActive;

    /// <summary>Only the Staff route carries the expander and the staff list beneath it.</summary>
    public bool IsStaffRoute => Id == ShellRouteId.Staff;

    /// <summary>HeroIcon sets Foreground locally, so the selected white has to arrive as a binding.</summary>
    public IBrush IconBrush => IsActive ? Brushes.White : AccentIcon;

    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(IconBrush));
}
