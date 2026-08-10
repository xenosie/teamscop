namespace Teamscop.Engine.Lifecycle;

/// <summary>
/// The directory ACL plan for a Teamscop install, as plain <c>icacls</c> argument strings so it can
/// be asserted without running the installer.
///
/// One line at <c>agent/Teamscop.Setup</c> caused three of the eight reported defects: it hardened
/// <c>%ProgramData%\Teamscop</c>, its <c>bin</c> AND its <c>Agent</c> subdir all to SYSTEM +
/// Administrators only. Because the Administrators SID is USE_FOR_DENY_ONLY in a UAC-filtered token,
/// nothing in a normal desktop session could read or execute anything under that tree — so the app
/// would not relaunch from its shortcut (defect 2), Settings could not run the uninstaller (defect
/// 1), and the SessionHelper could never start (the capture blackout behind defects 4/7).
///
/// The fix is the Program Files model: binaries a standard user must LAUNCH get Users read+execute;
/// only <c>Agent</c> — the vault, outbox, state and secrets §12.1 requires hiding from the employee
/// — stays SYSTEM+Administrators-only.
/// </summary>
public static class InstallerAcl
{
    public const string SystemSid = "S-1-5-18";           // NT AUTHORITY\SYSTEM
    public const string AdministratorsSid = "S-1-5-32-544"; // BUILTIN\Administrators
    public const string UsersSid = "S-1-5-32-545";          // BUILTIN\Users

    /// <summary>What a directory grants BUILTIN\Users.</summary>
    public enum UsersAccess
    {
        /// <summary>Nothing. <c>Agent</c> only — vault, outbox, approval secrets.</summary>
        None,

        /// <summary>Launch but not alter. <c>root</c> and <c>bin</c>.</summary>
        ReadExecute,

        /// <summary>
        /// Read and write. <c>Session</c> only, which holds the employee's own access token.
        ///
        /// Teamscop.App is asInvoker, so with anything less than this a staff Join wrote nothing and
        /// the enrolment vanished on the next close — while still reporting success.
        /// </summary>
        Modify
    }

    /// <summary>The <c>/grant:r</c> argument list for a directory.</summary>
    public static string GrantArguments(UsersAccess users)
    {
        var grant = $"*{SystemSid}:(OI)(CI)F *{AdministratorsSid}:(OI)(CI)F";
        return users switch
        {
            UsersAccess.ReadExecute => grant + $" *{UsersSid}:(OI)(CI)RX",
            UsersAccess.Modify => grant + $" *{UsersSid}:(OI)(CI)M",
            _ => grant
        };
    }

    /// <summary>Back-compat overload: true meant read+execute.</summary>
    public static string GrantArguments(bool usersReadExecute)
        => GrantArguments(usersReadExecute ? UsersAccess.ReadExecute : UsersAccess.None);

    /// <summary>What the given directory (relative to install root) grants Users.</summary>
    public static UsersAccess AccessFor(string subdirectory) => subdirectory switch
    {
        var s when string.Equals(s, "Agent", StringComparison.OrdinalIgnoreCase) => UsersAccess.None,
        var s when string.Equals(s, LocalAgentStore.StaffSessionDirectory, StringComparison.OrdinalIgnoreCase)
            => UsersAccess.Modify,
        _ => UsersAccess.ReadExecute
    };

    /// <summary>True when the given directory must grant Users at least read+execute.</summary>
    public static bool GrantsUsersReadExecute(string subdirectory)
        => AccessFor(subdirectory) != UsersAccess.None;
}
