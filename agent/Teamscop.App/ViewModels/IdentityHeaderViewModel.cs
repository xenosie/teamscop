using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Teamscop.App.Composition;
using Teamscop.App.Services;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.ViewModels;

/// <summary>
/// Who is signed in, shown at the top of the nav: company avatar and name, username, role pill,
/// team. Replaces the identical company/role blocks in MainViewModel and WorkspaceViewModel —
/// including the two code-behind ApplyAvatarImage helpers, since the avatar is now a binding.
/// </summary>
public sealed partial class IdentityHeaderViewModel : ObservableObject
{
    private readonly ImageLoader _images;
    private readonly UiLog _log;
    private string? _avatarUrl;

    public IdentityHeaderViewModel(AppServices services)
    {
        _images = services.Images;
        _log = services.Log;
    }

    [ObservableProperty] private string _companyName = "Teamscop";
    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string _roleLabel = "Staff";
    [ObservableProperty] private string? _teamName;
    [ObservableProperty] private Bitmap? _companyAvatar;

    public bool HasCompanyAvatar => CompanyAvatar is not null;
    public bool HasTeamName => !string.IsNullOrWhiteSpace(TeamName);

    public string Subtitle => string.IsNullOrWhiteSpace(UserName) ? RoleLabel : $"{UserName} · {RoleLabel}";

    /// <summary>Cached identity from local state — painted before any request goes out.</summary>
    public void Apply(LocalAgentState state)
    {
        CompanyName = string.IsNullOrWhiteSpace(state.CompanyName) ? "Teamscop" : state.CompanyName!;
        UserName = state.Username ?? string.Empty;
        LoadAvatar(state.CompanyAvatarUrl);
    }

    /// <summary>The role pill comes from capabilities, so a promotion re-labels it without a restart.</summary>
    public void Apply(ShellCapabilities capabilities, MyOrgPlacementDto? placement)
    {
        RoleLabel = capabilities.RoleLabel;
        TeamName = placement?.TeamName;
    }

    public void LoadAvatar(string? url)
    {
        if (string.Equals(_avatarUrl, url, StringComparison.Ordinal) && CompanyAvatar is not null)
        {
            return;
        }

        _avatarUrl = url;
        LoadAvatarAsync(url).FireAndForget(_log, "Company avatar");
    }

    private async Task LoadAvatarAsync(string? url)
    {
        var bitmap = await _images.LoadAsync(url).ConfigureAwait(false);
        if (bitmap is null)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() => CompanyAvatar = bitmap);
    }

    partial void OnCompanyAvatarChanged(Bitmap? value) => OnPropertyChanged(nameof(HasCompanyAvatar));

    partial void OnUserNameChanged(string value) => OnPropertyChanged(nameof(Subtitle));

    partial void OnRoleLabelChanged(string value) => OnPropertyChanged(nameof(Subtitle));

    partial void OnTeamNameChanged(string? value) => OnPropertyChanged(nameof(HasTeamName));
}
