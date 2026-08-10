namespace Teamscop.Engine.Lifecycle;

public enum AgentRole
{
    Admin = 1,
    Staff = 2
}

public sealed class RolePolicy
{
    public required AgentRole Role { get; init; }
    public bool AllowUserClose => Role == AgentRole.Admin;
    public bool RunsAsWindowsService => Role == AgentRole.Staff;
    public bool ShowDesktopShortcut => Role == AgentRole.Admin;
    public bool ShowStartMenuShortcut => Role == AgentRole.Admin;
    public bool RequireTotpForUninstall => Role == AgentRole.Staff;
    public string RecommendedInstallRoot => Role == AgentRole.Staff
        ? @"%ProgramData%\Teamscop\Agent"
        : @"%ProgramFiles%\Teamscop\Admin";

    public static RolePolicy For(AgentRole role) => new() { Role = role };
}
