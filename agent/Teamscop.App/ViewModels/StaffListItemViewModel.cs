using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Teamscop.App.ViewModels;

public sealed partial class StaffListItemViewModel : ObservableObject
{
    public Guid UserId { get; init; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private Bitmap? _avatar;
    [ObservableProperty] private string? _avatarUrl;
    [ObservableProperty] private bool _isSelected;

    public bool HasAvatar => Avatar is not null;

    partial void OnAvatarChanged(Bitmap? value)
        => OnPropertyChanged(nameof(HasAvatar));
}
