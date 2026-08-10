using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HeroIconsAvalonia.Enums;

namespace Teamscop.App.ViewModels;

public enum StaffSectionId
{
    Summary,
    Screenshot,
    Browsing,
    TimeTrack,
    AppHistory,
    TrackingSettings
}

/// <summary>
/// One staff sub-section: its label, the capability that reveals it, and the ViewModel the
/// content host renders. The declarative list of these replaces four hand-maintained copies of
/// "is this section allowed / pick a default / clamp" plus twelve IsStaffDetailX booleans.
/// </summary>
public sealed partial class StaffSectionViewModel : ObservableObject
{
    private static readonly IBrush AccentIcon = SolidColorBrush.Parse("#2563EB");
    private readonly Func<ShellCapabilities, bool> _allow;

    public StaffSectionViewModel(
        StaffSectionId id,
        string label,
        IconType icon,
        Func<ShellCapabilities, bool> allow,
        object content)
    {
        Id = id;
        Label = label;
        Icon = icon;
        _allow = allow;
        Content = content;
    }

    public StaffSectionId Id { get; }
    public string Label { get; }
    public IconType Icon { get; }
    public object Content { get; }

    [ObservableProperty] private bool _isActive;

    public IBrush IconBrush => IsActive ? Brushes.White : AccentIcon;

    public bool IsAllowed(ShellCapabilities capabilities) => _allow(capabilities);

    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(IconBrush));
}
