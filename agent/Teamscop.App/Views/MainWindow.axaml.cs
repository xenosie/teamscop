using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using HeroIconsAvalonia.Controls;
using Teamscop.App.ViewModels;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private static readonly IBrush AccentIcon = Brush.Parse("#2563EB");

    public MainWindow()
        : this(new LocalAgentStore(AgentRole.Admin).Load())
    {
    }

    public MainWindow(LocalAgentState state)
    {
        InitializeComponent();
        _vm = new MainViewModel(state);
        DataContext = _vm;
        _vm.TeamsBoard.RequestPickMember = (title, candidates) =>
            AddMemberDialog.PickAsync(this, title, candidates);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.Section)
                or nameof(MainViewModel.IsStaffs)
                or nameof(MainViewModel.IsTeams)
                or nameof(MainViewModel.IsLeaderboard)
                or nameof(MainViewModel.IsSettings)
                or nameof(MainViewModel.IsStaffsExpanded)
                or nameof(MainViewModel.StaffDetailSection)
                or nameof(MainViewModel.HasSelectedStaff)
                or nameof(MainViewModel.IsStaffDetailSummary)
                or nameof(MainViewModel.IsStaffDetailScreenshot)
                or nameof(MainViewModel.IsStaffDetailBrowsingHistory)
                or nameof(MainViewModel.IsStaffDetailTimeTrack)
                or nameof(MainViewModel.IsStaffDetailAppHistory))
            {
                RefreshSidebar();
            }

            if (e.PropertyName is nameof(MainViewModel.CompanyAvatar))
            {
                ApplyAvatarImage(_vm.CompanyAvatar);
            }
        };
        Opened += async (_, _) =>
        {
            RefreshSidebar();
            await _vm.RefreshProfileAsync();
            ApplyAvatarImage(_vm.CompanyAvatar);
        };
    }

    private void ApplyAvatarImage(Bitmap? bitmap)
    {
        if (bitmap is null)
        {
            CompanyAvatarPlaceholder.IsVisible = true;
            CompanyAvatarPlaceholder.Opacity = 1;
            return;
        }

        CompanyAvatarCircle.Fill = new ImageBrush(bitmap)
        {
            Stretch = Stretch.UniformToFill,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center
        };
        CompanyAvatarPlaceholder.IsVisible = false;
        CompanyAvatarPlaceholder.Opacity = 0;
    }

    private void RefreshSidebar()
    {
        SetSelected(BtnStaffs, IconStaffs, _vm.IsStaffs);
        SetSelected(BtnTeams, IconTeams, _vm.IsTeams);
        SetSelected(BtnLeaderboard, IconLeaderboard, _vm.IsLeaderboard);
        SetSelected(BtnSettings, IconSettings, _vm.IsSettings);

        var chevron = _vm.IsStaffs ? Brushes.White : AccentIcon;
        IconStaffsChevron.Foreground = chevron;
        IconStaffsChevronDown.Foreground = chevron;

        if (_vm.HasSelectedStaff)
        {
            SetSelected(BtnDetailSummary, IconDetailSummary, _vm.IsStaffDetailSummary);
            SetSelected(BtnDetailScreenshot, IconDetailScreenshot, _vm.IsStaffDetailScreenshot);
            SetSelected(BtnDetailBrowsing, IconDetailBrowsing, _vm.IsStaffDetailBrowsingHistory);
            SetSelected(BtnDetailTimeTrack, IconDetailTimeTrack, _vm.IsStaffDetailTimeTrack);
            SetSelected(BtnDetailAppHistory, IconDetailAppHistory, _vm.IsStaffDetailAppHistory);
        }
    }

    private static void SetSelected(Button button, HeroIcon icon, bool active)
    {
        button.Classes.Set("selected", active);
        icon.Foreground = active ? Brushes.White : AccentIcon;
    }
}
