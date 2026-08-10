using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Teamscop.App.Composition;
using Teamscop.App.Services;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.ViewModels;

public sealed partial class StaffCardViewModel : ObservableObject
{
    public Guid UserId { get; init; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private Bitmap? _avatar;
    [ObservableProperty] private string? _avatarUrl;
    [ObservableProperty] private Guid? _teamId;
    [ObservableProperty] private bool _isLeader;

    public bool HasAvatar => Avatar is not null;

    partial void OnAvatarChanged(Bitmap? value)
        => OnPropertyChanged(nameof(HasAvatar));

    public static StaffCardViewModel FromDto(OrgStaffDto dto, Guid? teamId = null, bool isLeader = false)
        => new()
        {
            UserId = dto.UserId,
            Name = dto.Username,
            AvatarUrl = dto.AvatarUrl,
            TeamId = teamId,
            IsLeader = isLeader
        };
}

public sealed partial class TeamBoxViewModel : ObservableObject
{
    public Guid TeamId { get; init; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private StaffCardViewModel? _leader;

    public ObservableCollection<StaffCardViewModel> Members { get; } = [];

    public bool HasLeader => Leader is not null;
    public bool HasNoLeader => Leader is null;

    partial void OnLeaderChanged(StaffCardViewModel? value)
    {
        OnPropertyChanged(nameof(HasLeader));
        OnPropertyChanged(nameof(HasNoLeader));
    }
}

public sealed partial class TeamsBoardViewModel : ObservableObject
{
    private readonly TeamscopApi _api;
    private readonly SessionStore _session;
    private readonly ImageLoader _images;
    private readonly UiLog _log;

    public TeamsBoardViewModel(AppServices services)
    {
        _api = services.Api;
        _session = services.Session;
        _images = services.Images;
        _log = services.Log;
    }

    public ObservableCollection<StaffCardViewModel> Pool { get; } = [];
    public ObservableCollection<TeamBoxViewModel> Teams { get; } = [];

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;

    /// <summary>title, candidates → selected user id or null.</summary>
    public Func<string, IReadOnlyList<StaffCardViewModel>, Task<Guid?>>? RequestPickMember { get; set; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (!_session.HasToken)
        {
            SetStatus("Sign in required.");
            return;
        }

        SetBusy(true);
        SetStatus(null);
        try
        {
            var structure = await _api.GetStructureAsync(ct).ConfigureAwait(false);
            await ApplyStructureAsync(structure).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The route was left; the next entry reloads.
        }
        catch (Exception ex)
        {
            _log.Warn("Teams board could not load", ex);
            SetStatus(Truncate(ApiError.Describe(ex)));
        }
        finally
        {
            SetBusy(false);
        }
    }

    [RelayCommand]
    private async Task CreateTeamAsync()
    {
        var n = Teams.Count + 1;
        await MutateAsync(ct => _api.CreateTeamAsync($"Team {n}", ct));
    }

    [RelayCommand]
    private async Task RenameTeamAsync(TeamBoxViewModel? team)
    {
        if (team is null || string.IsNullOrWhiteSpace(team.Name))
        {
            return;
        }

        await MutateAsync(ct => _api.UpdateTeamAsync(team.TeamId, name: team.Name.Trim(), ct: ct));
    }

    [RelayCommand]
    private async Task DeleteTeamAsync(TeamBoxViewModel? team)
    {
        if (team is null)
        {
            return;
        }

        await MutateAsync(ct => _api.DeleteTeamAsync(team.TeamId, ct));
    }

    [RelayCommand]
    private async Task AddLeaderAsync(TeamBoxViewModel? team)
    {
        if (team is null || RequestPickMember is null)
        {
            return;
        }

        if (Pool.Count == 0)
        {
            StatusMessage = "No staff left in the pool.";
            return;
        }

        var picked = await RequestPickMember.Invoke("Add leader", Pool.ToList());
        if (picked is null)
        {
            return;
        }

        await MutateAsync(ct => _api.UpdateTeamAsync(team.TeamId, leaderUserId: picked.Value, ct: ct));
    }

    [RelayCommand]
    private async Task SwitchLeaderAsync(TeamBoxViewModel? team)
    {
        if (team is null || RequestPickMember is null)
        {
            return;
        }

        // Pool + current members (not the current leader)
        var candidates = Pool
            .Concat(team.Members)
            .Where(s => team.Leader is null || s.UserId != team.Leader.UserId)
            .GroupBy(s => s.UserId)
            .Select(g => g.First())
            .ToList();

        if (candidates.Count == 0)
        {
            StatusMessage = "No one available to switch in.";
            return;
        }

        var picked = await RequestPickMember.Invoke("Switch leader", candidates);
        if (picked is null)
        {
            return;
        }

        await MutateAsync(ct => _api.UpdateTeamAsync(team.TeamId, leaderUserId: picked.Value, ct: ct));
    }

    [RelayCommand]
    private async Task AddMemberAsync(TeamBoxViewModel? team)
    {
        if (team is null || RequestPickMember is null)
        {
            return;
        }

        if (Pool.Count == 0)
        {
            StatusMessage = "No staff left in the pool.";
            return;
        }

        var picked = await RequestPickMember.Invoke("Add member", Pool.ToList());
        if (picked is null)
        {
            return;
        }

        await MutateAsync(ct => _api.AddTeamMemberAsync(team.TeamId, picked.Value, ct));
    }

    [RelayCommand]
    private async Task RemovePersonAsync(StaffCardViewModel? person)
    {
        if (person?.TeamId is not Guid teamId)
        {
            return;
        }

        await MutateAsync(ct => person.IsLeader
            ? _api.UpdateTeamAsync(teamId, clearLeader: true, ct: ct)
            : _api.RemoveTeamMemberAsync(teamId, person.UserId, ct));
    }

    private async Task MutateAsync(Func<CancellationToken, Task> action, CancellationToken ct = default)
    {
        if (!_session.HasToken)
        {
            SetStatus("Sign in required.");
            return;
        }

        SetBusy(true);
        SetStatus(null);
        try
        {
            await action(ct).ConfigureAwait(false);
            var structure = await _api.GetStructureAsync(ct).ConfigureAwait(false);
            await ApplyStructureAsync(structure).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("Teams board mutation failed", ex);
            SetStatus(Truncate(ApiError.Describe(ex)));
            try
            {
                // Re-read: the mutation may have half-applied, and the board must show the truth.
                var structure = await _api.GetStructureAsync(ct).ConfigureAwait(false);
                await ApplyStructureAsync(structure).ConfigureAwait(false);
            }
            catch (Exception refreshEx)
            {
                _log.Warn("Teams board refresh after a failed mutation also failed", refreshEx);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Both continuations here run off the UI thread, so a failure used to raise PropertyChanged
    /// from a thread pool thread — the binding never applied and a broken board looked empty.
    /// </summary>
    private void SetStatus(string? message) => OnUi(() => StatusMessage = message);

    private void SetBusy(bool value) => OnUi(() => IsBusy = value);

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }

    private async Task ApplyStructureAsync(OrgStructureDto structure)
    {
        var pool = structure.UnassignedStaff.Select(s => StaffCardViewModel.FromDto(s)).ToList();
        var teams = new List<TeamBoxViewModel>();
        foreach (var t in structure.Teams)
        {
            var box = new TeamBoxViewModel
            {
                TeamId = t.TeamId,
                Name = t.Name,
                Leader = t.Leader is null
                    ? null
                    : StaffCardViewModel.FromDto(t.Leader, t.TeamId, isLeader: true)
            };
            foreach (var m in t.Members)
            {
                box.Members.Add(StaffCardViewModel.FromDto(m, t.TeamId));
            }

            teams.Add(box);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Pool.Clear();
            foreach (var p in pool)
            {
                Pool.Add(p);
            }

            Teams.Clear();
            foreach (var t in teams)
            {
                Teams.Add(t);
            }
        });

        var all = pool.Concat(teams.SelectMany(t =>
        {
            IEnumerable<StaffCardViewModel> list = t.Members;
            if (t.Leader is not null)
            {
                list = list.Prepend(t.Leader);
            }

            return list;
        }));

        foreach (var card in all)
        {
            LoadAvatarAsync(card).FireAndForget(_log, "Team member avatar");
        }
    }

    private async Task LoadAvatarAsync(StaffCardViewModel card)
    {
        var bitmap = await _images.LoadAsync(card.AvatarUrl).ConfigureAwait(false);
        if (bitmap is null)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() => card.Avatar = bitmap);
    }

    private static string Truncate(string message)
    {
        const int max = 180;
        return message.Length <= max ? message : message[..max] + "…";
    }
}
