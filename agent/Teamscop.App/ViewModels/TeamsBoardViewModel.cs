using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Teamscop.App.Services;
using CommunityToolkit.Mvvm.Input;
using Teamscop.Engine.Auth;
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
    private readonly LocalAgentStore _store = AppSessionStore.ForActiveSession();
    private string _apiBaseUrl;
    private readonly Dictionary<Guid, Bitmap> _avatarCache = new();

    public TeamsBoardViewModel(string? apiBaseUrl = null)
    {
        _apiBaseUrl = ResolveApiBase(apiBaseUrl);
    }

    public ObservableCollection<StaffCardViewModel> Pool { get; } = [];
    public ObservableCollection<TeamBoxViewModel> Teams { get; } = [];

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;

    /// <summary>title, candidates → selected user id or null.</summary>
    public Func<string, IReadOnlyList<StaffCardViewModel>, Task<Guid?>>? RequestPickMember { get; set; }

    public async Task LoadAsync()
    {
        var state = _store.Load();
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            StatusMessage = "Sign in required.";
            return;
        }

        _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
        IsBusy = true;
        StatusMessage = null;
        try
        {
            using var org = new OrgApiClient(_apiBaseUrl);
            var structure = await org.GetStructureAsync(state.AccessToken).ConfigureAwait(false);
            await ApplyStructureAsync(structure).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StatusMessage = Truncate(FormatClientError(ex));
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    [RelayCommand]
    private async Task CreateTeamAsync()
    {
        var n = Teams.Count + 1;
        await MutateAsync(async (org, token) =>
        {
            await org.CreateTeamAsync(token, $"Team {n}").ConfigureAwait(false);
        });
    }

    [RelayCommand]
    private async Task RenameTeamAsync(TeamBoxViewModel? team)
    {
        if (team is null || string.IsNullOrWhiteSpace(team.Name))
        {
            return;
        }

        await MutateAsync(async (org, token) =>
        {
            await org.UpdateTeamAsync(token, team.TeamId, name: team.Name.Trim()).ConfigureAwait(false);
        });
    }

    [RelayCommand]
    private async Task DeleteTeamAsync(TeamBoxViewModel? team)
    {
        if (team is null)
        {
            return;
        }

        await MutateAsync(async (org, token) =>
        {
            await org.DeleteTeamAsync(token, team.TeamId).ConfigureAwait(false);
        });
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

        await MutateAsync(async (org, token) =>
        {
            await org.UpdateTeamAsync(token, team.TeamId, leaderUserId: picked.Value).ConfigureAwait(false);
        });
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

        await MutateAsync(async (org, token) =>
        {
            await org.UpdateTeamAsync(token, team.TeamId, leaderUserId: picked.Value).ConfigureAwait(false);
        });
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

        await MutateAsync(async (org, token) =>
        {
            await org.AddMemberAsync(token, team.TeamId, picked.Value).ConfigureAwait(false);
        });
    }

    [RelayCommand]
    private async Task RemovePersonAsync(StaffCardViewModel? person)
    {
        if (person?.TeamId is not Guid teamId)
        {
            return;
        }

        await MutateAsync(async (org, token) =>
        {
            if (person.IsLeader)
            {
                await org.UpdateTeamAsync(token, teamId, clearLeader: true).ConfigureAwait(false);
            }
            else
            {
                await org.RemoveMemberAsync(token, teamId, person.UserId).ConfigureAwait(false);
            }
        });
    }

    private async Task MutateAsync(Func<OrgApiClient, string, Task> action)
    {
        var state = _store.Load();
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            StatusMessage = "Sign in required.";
            return;
        }

        _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
        IsBusy = true;
        StatusMessage = null;
        try
        {
            using var org = new OrgApiClient(_apiBaseUrl);
            await action(org, state.AccessToken).ConfigureAwait(false);
            var structure = await org.GetStructureAsync(state.AccessToken).ConfigureAwait(false);
            await ApplyStructureAsync(structure).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StatusMessage = Truncate(FormatClientError(ex));
            try
            {
                using var org = new OrgApiClient(_apiBaseUrl);
                var structure = await org.GetStructureAsync(state.AccessToken).ConfigureAwait(false);
                await ApplyStructureAsync(structure).ConfigureAwait(false);
            }
            catch
            {
                // keep error
            }
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
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
            _ = LoadAvatarAsync(card);
        }
    }

    private async Task LoadAvatarAsync(StaffCardViewModel card)
    {
        if (_avatarCache.TryGetValue(card.UserId, out var cached))
        {
            await Dispatcher.UIThread.InvokeAsync(() => card.Avatar = cached);
            return;
        }

        var absolute = ToAbsoluteUrl(card.AvatarUrl);
        if (absolute is null)
        {
            return;
        }

        try
        {
            byte[] bytes;
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
            {
                bytes = await http.GetByteArrayAsync(absolute).ConfigureAwait(false);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                using var ms = new MemoryStream(bytes);
                var bmp = new Bitmap(ms);
                _avatarCache[card.UserId] = bmp;
                card.Avatar = bmp;
            });
        }
        catch
        {
            // placeholder
        }
    }

    private string? ToAbsoluteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var abs)
            && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
        {
            return abs.ToString();
        }

        return $"{_apiBaseUrl.TrimEnd('/')}/{url.TrimStart('/')}";
    }

    private static string ResolveApiBase(string? apiBaseUrl)
        => string.IsNullOrWhiteSpace(apiBaseUrl)
            ? Environment.GetEnvironmentVariable("TEAMSCOP_API_BASE") ?? "https://teamscop.com"
            : apiBaseUrl.TrimEnd('/');

    private static string FormatClientError(Exception ex)
    {
        while (ex is AggregateException { InnerException: { } inner })
        {
            ex = inner;
        }

        if (ex is ApiClientException api)
        {
            var msg = api.Message;
            foreach (var prefix in new[] { "Org API: ", "Auth API: ", "Lifecycle API: " })
            {
                if (msg.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return msg[prefix.Length..];
                }
            }

            return msg;
        }

        return ex is HttpRequestException
            ? "Could not reach the server."
            : (string.IsNullOrWhiteSpace(ex.Message) ? "Something went wrong." : ex.Message);
    }

    private static string Truncate(string message)
    {
        const int max = 180;
        return message.Length <= max ? message : message[..max] + "…";
    }
}
