using Avalonia.Controls;
using Teamscop.App.Views;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.Services;

/// <summary>
/// Opens the correct desktop shell for admin / team leader / policeman / merged officer.
/// </summary>
public static class RoleShellRouter
{
    public static async Task<Window> CreateWindowAsync(LocalAgentState state)
    {
        AppSessionStore.SetActive(state.Role);

        if (AppSessionStore.IsAdminRole(state.Role))
        {
            return new MainWindow(state);
        }

        var kind = await ResolveKindAsync(state).ConfigureAwait(true);
        return kind switch
        {
            RoleShellKind.Leader => new LeaderWorkspaceWindow(state),
            RoleShellKind.Police => new PoliceWorkspaceWindow(state),
            RoleShellKind.Officer => new OfficerWorkspaceWindow(state),
            RoleShellKind.StaffSticker => new TimeTrackStickerWindow(state),
            _ => new NoAccessWorkspaceWindow(state)
        };
    }

    public static async Task<RoleShellKind> ResolveKindAsync(LocalAgentState state)
    {
        if (AppSessionStore.IsAdminRole(state.Role))
        {
            return RoleShellKind.Admin;
        }

        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            return RoleShellKind.NoAccess;
        }

        var apiBase = string.IsNullOrWhiteSpace(state.ApiBaseUrl)
            ? Environment.GetEnvironmentVariable("TEAMSCOP_API_BASE") ?? "https://teamscop.com"
            : state.ApiBaseUrl.TrimEnd('/');

        var isLeader = false;
        var isPoliceCapable = false;

        try
        {
            using var org = new OrgApiClient(apiBase);
            var placement = await org.GetMyPlacementAsync(state.AccessToken).ConfigureAwait(false);
            isLeader = placement.IsTeamLeader;
        }
        catch
        {
            // placement optional for kind resolve
        }

        try
        {
            using var police = new PoliceApiClient(apiBase);
            var auth = await police.GetMineAsync(state.AccessToken).ConfigureAwait(false);
            // Policeman flag (package grants) — not inherent team-leader view packages alone.
            isPoliceCapable = auth.IsPoliceman;
        }
        catch
        {
            // authorities optional
        }

        if (isLeader && isPoliceCapable)
        {
            return RoleShellKind.Officer;
        }

        if (isLeader)
        {
            return RoleShellKind.Leader;
        }

        if (isPoliceCapable)
        {
            return RoleShellKind.Police;
        }

        // Signed-in plain staff: floating 24h timetrack sticker (no workspace).
        return RoleShellKind.StaffSticker;
    }
}
