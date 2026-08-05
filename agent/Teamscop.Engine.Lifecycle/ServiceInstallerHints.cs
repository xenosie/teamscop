namespace Teamscop.Engine.Lifecycle;

/// <summary>
/// Windows Service install contract for staff agents.
/// Actual sc.exe / WiX actions run on Windows during MSI install.
/// </summary>
public static class ServiceInstallerHints
{
    public const string ServiceName = "TeamscopStaff";
    public const string DisplayName = "Teamscop Staff Agent";
    public const string Description = "Teamscop background agent. Managed by company admin.";

    /// <summary>
    /// sc.exe create/config commands used by the Windows installer custom action.
    /// </summary>
    public static IReadOnlyList<string> BuildInstallCommands(string binaryPath)
    {
        var quoted = $"\"{binaryPath}\"";
        return
        [
            $"sc.exe create {ServiceName} binPath= {quoted} start= auto DisplayName= \"{DisplayName}\"",
            $"sc.exe description {ServiceName} \"{Description}\"",
            $"sc.exe failure {ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/5000",
            $"sc.exe start {ServiceName}"
        ];
    }

    public static IReadOnlyList<string> BuildUninstallCommands()
        =>
        [
            $"sc.exe stop {ServiceName}",
            $"sc.exe delete {ServiceName}"
        ];
}
