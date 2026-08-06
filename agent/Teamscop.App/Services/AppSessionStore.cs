using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.Services;

/// <summary>
/// Resolves the LocalAgentStore for the active App session (admin or staff/leader).
/// </summary>
public static class AppSessionStore
{
    private static AgentRole? _activeRole;

    public static bool IsAdminRole(string? role)
        => string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase);

    public static AgentRole ToAgentRole(string? role)
        => IsAdminRole(role) ? AgentRole.Admin : AgentRole.Staff;

    public static LocalAgentStore Create(string? role)
        => new(ToAgentRole(role));

    public static void SetActive(string? role)
        => _activeRole = ToAgentRole(role);

    public static void SetActive(AgentRole role)
        => _activeRole = role;

    /// <summary>
    /// Store for the session currently driving the App shell.
    /// Falls back to admin-then-staff when not yet set.
    /// </summary>
    public static LocalAgentStore ForActiveSession()
    {
        if (_activeRole is { } active)
        {
            return new LocalAgentStore(active);
        }

        var admin = new LocalAgentStore(AgentRole.Admin);
        if (!string.IsNullOrWhiteSpace(admin.Load().AccessToken))
        {
            return admin;
        }

        return new LocalAgentStore(AgentRole.Staff);
    }
}
