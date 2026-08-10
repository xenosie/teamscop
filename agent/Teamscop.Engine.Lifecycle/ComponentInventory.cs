namespace Teamscop.Engine.Lifecycle;

/// <summary>
/// Which binaries each half of the product expects to find on disk, and a check for the ones that
/// are gone.
///
/// The failure this exists for is a gutted install. Deleting files out of
/// <c>%ProgramData%\Teamscop\bin</c> does not stop anything that is already running: a live process
/// holds its code in memory and keeps reporting healthy while the product has in fact been taken
/// apart, and the damage only surfaces at the next reboot, when the deleted half never comes back.
/// So each side attests the OTHER side's files — <see cref="ServiceSideComponents"/> is what the
/// service looks for, <see cref="AppSideComponents"/> what the desktop app looks for — and whichever
/// half survives the deletion is the one still able to report it.
///
/// This checks EXISTENCE ONLY, and deliberately not hashes or signatures. Staff install Teamscop
/// themselves and are local administrators on their own machines by design, so anyone who can
/// replace a binary can equally well replace the checker that would object: content verification
/// here is anti-tamper theatre, not a control. It also has a real cost — every legitimate upgrade
/// changes every hash, so a content check reports the whole fleet as sabotaged on release day. A
/// name that is simply absent carries no such ambiguity.
///
/// Everything here is pure path handling: no P/Invoke, no registry, no session state. That matters
/// beyond portability, because the callers run in very different places. The service side reads
/// while sitting in session 0, which is where it also sits at the logon screen and on a locked
/// workstation; the app side reads from whichever interactive session the employee logged into,
/// which over RDP or after a fast user switch is a different session with a different token. A
/// machine-wide path under <c>%ProgramData%</c> reads identically from all of them, so none of those
/// states changes the answer. The one thing that does vary between them is ACCESS, and that is
/// handled below.
/// </summary>
public static class ComponentInventory
{
    /// <summary>
    /// The binaries the staff service attests: the four it does not own the lifetime of, so their
    /// removal leaves no trace the service would otherwise see.
    ///
    /// <c>Teamscop.SessionHelper.exe</c> is the partial exception and is listed on purpose. The
    /// service does relaunch the helper, and notices a missing helper binary at the moment it next
    /// tries — but that attempt only happens when the helper is not already running, so a deletion
    /// while the helper is alive stays invisible for the rest of the logon. Listing it here makes
    /// that deletion visible on the next sweep instead of at the next reboot.
    /// </summary>
    public static IReadOnlyList<string> ServiceSideComponents { get; } =
    [
        "Teamscop.App.exe",
        "Teamscop.SessionHelper.exe",
        "Teamscop.UninstallGuard.exe",
        "Teamscop.UsbApproval.exe"
    ];

    /// <summary>
    /// The binaries the desktop app attests. The service is the half an employee has the strongest
    /// motive to remove, and the app is the half still running in their session to notice.
    /// </summary>
    public static IReadOnlyList<string> AppSideComponents { get; } =
    [
        "Teamscop.StaffService.exe",
        "Teamscop.SessionHelper.exe"
    ];

    /// <summary>
    /// Where <c>Teamscop.Setup</c> extracts the payload: <c>%ProgramData%\Teamscop\bin</c>, resolved
    /// the same way the installer resolves it so the two cannot drift. Off Windows this is the
    /// platform's shared-data directory, which keeps the type constructible under the Linux test
    /// suite; nothing is installed there, so a test passes an explicit directory.
    ///
    /// Empty when the shared-data directory cannot be resolved to an absolute path.
    /// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> returns an empty string
    /// for a folder it cannot resolve, and combining that yields the RELATIVE path
    /// <c>Teamscop\bin</c>, which a Windows service would then resolve against its working directory
    /// of <c>C:\Windows\System32</c> and correctly find to be absent. That is a fabricated report of
    /// a deleted install, so an unresolvable base degrades to empty instead, which
    /// <see cref="MissingFrom"/> treats as inconclusive.
    /// </summary>
    public static string DefaultBinDirectory { get; } = ResolveDefaultBinDirectory();

    /// <summary>
    /// The <paramref name="expected"/> names that are not present in <paramref name="binDirectory"/>,
    /// in the order given and without duplicates. Empty means the install is intact as far as this
    /// reporter can see.
    ///
    /// A directory that genuinely does not exist is the whole install being gone, so every expected
    /// name comes back missing. Anything else that goes wrong — a denied read, a path the OS will not
    /// accept, an IO failure — returns empty instead: this result accuses an employee of gutting the
    /// product, and a failure to READ the directory is not evidence that anything was removed from
    /// it. Under-reporting costs one cycle of a check that repeats; over-reporting is an accusation
    /// that cannot be taken back.
    ///
    /// That distinction is the reason absence is established by letting the enumeration fail rather
    /// than by asking <see cref="Directory.Exists(string)"/> first. Directory.Exists reports false for
    /// a directory it merely cannot reach — it swallows access denials and answers exactly as it does
    /// for a directory that was deleted. A single ACL mistake on the install tree would therefore have
    /// every healthy machine in the fleet reporting its entire install stripped, which is the failure
    /// this whole check exists to raise. Enumeration separates the two: absent throws
    /// <see cref="DirectoryNotFoundException"/>, unreachable throws
    /// <see cref="UnauthorizedAccessException"/>. That is not hypothetical here — the ACL plan in
    /// <see cref="InstallerAcl"/> exists because one install did lock this exact directory away from
    /// the desktop session.
    ///
    /// A present but EMPTY directory does report every name, and should: it enumerates successfully,
    /// so the emptiness is observed rather than assumed.
    ///
    /// Callers sweep on a timer, so a report is a single sample. An upgrade replaces these files in
    /// place, and a sample landing between the delete and the write sees a name genuinely absent on a
    /// healthy machine. Require the same name across consecutive sweeps before treating it as real.
    ///
    /// Entries in <paramref name="expected"/> are bare file names, matched case-insensitively because
    /// the directory this describes lives on a case-insensitive filesystem.
    /// </summary>
    public static IReadOnlyList<string> MissingFrom(string binDirectory, IReadOnlyList<string> expected)
    {
        if (expected is null || expected.Count == 0 || string.IsNullOrWhiteSpace(binDirectory))
        {
            // A blank path or an empty request is a caller mistake, not a stripped install, and must
            // not be reported as one.
            return Array.Empty<string>();
        }

        var wanted = BareNames(expected);
        if (wanted.Count == 0)
        {
            return Array.Empty<string>();
        }

        try
        {
            // One listing rather than a File.Exists per name: enumeration surfaces an access failure
            // as an exception this method can tell apart from absence, whereas File.Exists answers
            // "false" to both and would turn a denied read into a sabotage report. The enumerator
            // holds a directory handle, and the foreach disposes it on every path out of the loop
            // including the throwing one, which matters for a sweep that repeats forever.
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(binDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                present.Add(Path.GetFileName(path));
            }

            return wanted.Where(name => !present.Contains(name)).ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            // The only failure that means what it says: the path is not there. Must be caught ahead of
            // IOException, which it derives from.
            return wanted;
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or System.Security.SecurityException
                                       or ArgumentException
                                       or NotSupportedException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Deduplicates while keeping the caller's order, dropping blanks and anything that is not a bare
    /// file name. A qualified name could never match the bare names this compares against, so it would
    /// report as missing forever; dropping it keeps a caller's malformed input from reading as
    /// sabotage, on the same grounds as a blank directory.
    /// </summary>
    private static List<string> BareNames(IReadOnlyList<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(names.Count);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var trimmed = name.Trim();
            if (IsBareFileName(trimmed) && seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    /// <summary>
    /// Whether the name carries no path of its own. The separators are tested literally rather than
    /// through <see cref="Path.GetFileName(string)"/> because a backslash is a separator on Windows
    /// and an ordinary filename character on Linux, and the answer must not depend on which of the two
    /// is running the check.
    /// </summary>
    private static bool IsBareFileName(string name)
        => name.IndexOfAny(['/', '\\', ':']) < 0;

    /// <summary>
    /// The installer's payload directory, or empty when the shared-data root does not resolve to an
    /// absolute path. See <see cref="DefaultBinDirectory"/> for why empty beats a relative path.
    /// </summary>
    private static string ResolveDefaultBinDirectory()
    {
        var shared = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(shared) && OperatingSystem.IsWindows())
        {
            shared = Environment.GetEnvironmentVariable("ProgramData") ?? "";
        }

        return string.IsNullOrWhiteSpace(shared) || !Path.IsPathRooted(shared)
            ? ""
            : Path.Combine(shared, "Teamscop", "bin");
    }
}
