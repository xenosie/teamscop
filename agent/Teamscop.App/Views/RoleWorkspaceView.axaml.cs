using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using HeroIconsAvalonia.Controls;
using Teamscop.App.ViewModels;

namespace Teamscop.App.Views;

public partial class RoleWorkspaceView : UserControl
{
    private static readonly IBrush AccentIcon = Brush.Parse("#2563EB");
    private WorkspaceViewModel? _vm;

    public RoleWorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireVm();
    }

    private void WireVm()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
        }

        _vm = DataContext as WorkspaceViewModel;
        if (_vm is null)
        {
            return;
        }

        _vm.PropertyChanged += OnVmChanged;
        ApplyAvatar(_vm.CompanyAvatar);
        RefreshSidebar();
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceViewModel.CompanyAvatar))
        {
            ApplyAvatar(_vm?.CompanyAvatar);
        }

        if (e.PropertyName is nameof(WorkspaceViewModel.Section)
            or nameof(WorkspaceViewModel.IsStaffs)
            or nameof(WorkspaceViewModel.IsCodes)
            or nameof(WorkspaceViewModel.IsTeams)
            or nameof(WorkspaceViewModel.IsStaffsExpanded)
            or nameof(WorkspaceViewModel.StaffDetailSection)
            or nameof(WorkspaceViewModel.HasSelectedStaff)
            or nameof(WorkspaceViewModel.ShowCodesNav)
            or nameof(WorkspaceViewModel.ShowTeamsNav)
            or nameof(WorkspaceViewModel.ShowAppHistoryTab))
        {
            RefreshSidebar();
        }
    }

    private void ApplyAvatar(Bitmap? bitmap)
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
        if (_vm is null)
        {
            return;
        }

        SetSelected(BtnStaffs, IconStaffs, _vm.IsStaffs);
        if (BtnCodes.IsVisible)
        {
            SetSelected(BtnCodes, IconCodes, _vm.IsCodes);
        }

        if (BtnTeams.IsVisible)
        {
            SetSelected(BtnTeams, IconTeams, _vm.IsTeams);
        }

        var chevron = _vm.IsStaffs ? Brushes.White : AccentIcon;
        IconStaffsChevron.Foreground = chevron;
        IconStaffsChevronDown.Foreground = chevron;

        if (_vm.HasSelectedStaff)
        {
            SetSelected(BtnDetailSummary, IconDetailSummary, _vm.IsStaffDetailSummary);
            SetSelected(BtnDetailScreenshot, IconDetailScreenshot, _vm.IsStaffDetailScreenshot);
            SetSelected(BtnDetailBrowsing, IconDetailBrowsing, _vm.IsStaffDetailBrowsingHistory);
            SetSelected(BtnDetailTimeTrack, IconDetailTimeTrack, _vm.IsStaffDetailTimeTrack);
            if (BtnDetailAppHistory.IsVisible)
            {
                SetSelected(BtnDetailAppHistory, IconDetailAppHistory, _vm.IsStaffDetailAppHistory);
            }
        }
    }

    private static void SetSelected(Button button, HeroIcon icon, bool active)
    {
        button.Classes.Set("selected", active);
        icon.Foreground = active ? Brushes.White : AccentIcon;
    }
}
