using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Teamscop.App.Composition;

namespace Teamscop.App.ViewModels;

public sealed partial class StaffListItemViewModel : ObservableObject
{
    private static readonly IBrush WorkingBrush = SolidColorBrush.Parse("#16A34A");
    private static readonly IBrush RestBrush = SolidColorBrush.Parse("#DC2626");
    private static readonly IBrush OfflineBrush = SolidColorBrush.Parse("#94A3B8");

    // §14 — the engine-status dot. Black is deliberately the loudest colour on the row: it is the
    // only one that says a person did something to their machine, so it should be hard to miss and
    // hard to confuse with the everyday red of "not at the keyboard right now".
    private static readonly IBrush StatusOnlineBrush = SolidColorBrush.Parse("#16A34A");
    private static readonly IBrush StatusOfflineBrush = SolidColorBrush.Parse("#94A3B8");
    private static readonly IBrush StatusBrokenBrush = SolidColorBrush.Parse("#111827");
    private static readonly IBrush StatusUninstalledBrush = SolidColorBrush.Parse("#EAB308");

    public Guid UserId { get; init; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private Bitmap? _avatar;
    [ObservableProperty] private string? _avatarUrl;
    [ObservableProperty] private bool _isSelected;

    /// <summary>§5.1 — live presence, mapped onto the row's badge: green working / red rest / grey offline.</summary>
    [ObservableProperty] private StaffPresenceState _presence = StaffPresenceState.Unknown;

    public bool HasAvatar => Avatar is not null;

    /// <summary>The badge is drawn only once presence is actually known, never as a guess.</summary>
    public bool HasPresence => Presence != StaffPresenceState.Unknown;

    public IBrush StatusBrush => Presence switch
    {
        StaffPresenceState.Working => WorkingBrush,
        StaffPresenceState.Rest => RestBrush,
        _ => OfflineBrush
    };

    public string StatusLabel => Presence switch
    {
        StaffPresenceState.Working => "Working",
        StaffPresenceState.Rest => "Resting",
        StaffPresenceState.Offline => "Offline",
        _ => string.Empty
    };

    /// <summary>§14 — the machine's engine status, independent of whether anyone is typing.</summary>
    [ObservableProperty] private StaffAgentStatusState _agentStatus = StaffAgentStatusState.Unknown;

    /// <summary>Why, in the server's words. Shown as the dot's tooltip.</summary>
    [ObservableProperty] private string? _agentStatusReason;

    /// <summary>Drawn only once the status is actually known, never as a guess.</summary>
    public bool HasAgentStatus => AgentStatus != StaffAgentStatusState.Unknown;

    public IBrush AgentStatusBrush => AgentStatus switch
    {
        StaffAgentStatusState.Online => StatusOnlineBrush,
        StaffAgentStatusState.Broken => StatusBrokenBrush,
        StaffAgentStatusState.Uninstalled => StatusUninstalledBrush,
        _ => StatusOfflineBrush
    };

    public string AgentStatusLabel => AgentStatus switch
    {
        StaffAgentStatusState.Online => "Online",
        StaffAgentStatusState.Offline => "Offline",
        StaffAgentStatusState.Broken => "Broken",
        StaffAgentStatusState.Uninstalled => "Not installed",
        _ => string.Empty
    };

    /// <summary>
    /// The dot alone cannot say "the helper has been dead for ten minutes" or "Teamscop.App.exe was
    /// deleted", and those are the facts an admin actually needs, so the reason rides in the tooltip.
    /// </summary>
    public string AgentStatusTooltip
        => string.IsNullOrWhiteSpace(AgentStatusReason)
            ? AgentStatusLabel
            : $"{AgentStatusLabel} — {AgentStatusReason}";

    /// <summary>
    /// ONE badge per row, answering both questions in priority order.
    ///
    /// Two dots — activity on the avatar, engine status by the name — read as clutter and confused
    /// their owner within a day. Merged: anything wrong with the machine outranks whether someone is
    /// typing (black broken, gold uninstalled, grey unreachable), and only a healthy machine shows
    /// the working/resting split. Same colours as before, one place to look.
    /// </summary>
    public IBrush CombinedBadgeBrush => AgentStatus switch
    {
        StaffAgentStatusState.Broken => StatusBrokenBrush,
        StaffAgentStatusState.Uninstalled => StatusUninstalledBrush,
        StaffAgentStatusState.Offline => StatusOfflineBrush,
        StaffAgentStatusState.Online => Presence == StaffPresenceState.Working ? WorkingBrush : RestBrush,
        // Status unknown (older server): fall back to plain activity so the row is not blank.
        _ => Presence switch
        {
            StaffPresenceState.Working => WorkingBrush,
            StaffPresenceState.Rest => RestBrush,
            _ => OfflineBrush
        }
    };

    public bool HasBadge => AgentStatus != StaffAgentStatusState.Unknown || Presence != StaffPresenceState.Unknown;

    public string CombinedBadgeTooltip => AgentStatus switch
    {
        StaffAgentStatusState.Online =>
            (Presence == StaffPresenceState.Working ? "Working" : "Resting") + " · machine online",
        StaffAgentStatusState.Unknown => StatusLabel,
        _ => AgentStatusTooltip
    };

    partial void OnAgentStatusChanged(StaffAgentStatusState value)
    {
        OnPropertyChanged(nameof(HasAgentStatus));
        OnPropertyChanged(nameof(AgentStatusBrush));
        OnPropertyChanged(nameof(AgentStatusLabel));
        OnPropertyChanged(nameof(AgentStatusTooltip));
        OnPropertyChanged(nameof(CombinedBadgeBrush));
        OnPropertyChanged(nameof(CombinedBadgeTooltip));
        OnPropertyChanged(nameof(HasBadge));
    }

    partial void OnAgentStatusReasonChanged(string? value)
    {
        OnPropertyChanged(nameof(AgentStatusTooltip));
        OnPropertyChanged(nameof(CombinedBadgeTooltip));
    }

    /// <summary>Set once the row has asked for its avatar, so re-realising it costs nothing.</summary>
    public bool AvatarRequested { get; set; }

    partial void OnAvatarChanged(Bitmap? value)
        => OnPropertyChanged(nameof(HasAvatar));

    partial void OnPresenceChanged(StaffPresenceState value)
    {
        OnPropertyChanged(nameof(HasPresence));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(CombinedBadgeBrush));
        OnPropertyChanged(nameof(CombinedBadgeTooltip));
        OnPropertyChanged(nameof(HasBadge));
    }
}
