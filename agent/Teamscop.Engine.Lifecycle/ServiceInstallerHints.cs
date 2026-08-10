namespace Teamscop.Engine.Lifecycle;

/// <summary>
/// Windows Service install contract for staff agents.
/// Teamscop.Setup keeps its own copy of these three strings — it has no project references by
/// design, because it ships as a single self-contained exe with the payload embedded. This file is
/// the canonical one; if a name changes here, change it there too.
/// </summary>
public static class ServiceInstallerHints
{
    public const string ServiceName = "TeamscopStaff";
    public const string DisplayName = "Teamscop Staff Agent";
    public const string Description = "Teamscop background agent. Managed by company admin.";
}
