using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Teamscop.Engine.Tracking;

/// <summary>
/// One read of the machine's interactive session table, reduced to the three facts the tracking
/// loop actually reasons about.
///
/// The distinction that matters is between "nobody is there" and "somebody is there but capture is
/// broken". Only the second is a fault worth reporting; the first is an employee who went home.
/// </summary>
/// <param name="AnyUserLoggedOn">At least one session is owned by a named user, connected or not.</param>
/// <param name="ActiveConsoleUser">
/// A named user is connected at the physical console over the local protocol. This is a necessary
/// condition for a screen capture to mean anything, not a sufficient one: a locked workstation
/// stays <c>WTSActive</c> while the secure desktop is up, and capture then yields a blank frame
/// rather than an error. Callers must treat this as "a failed capture is worth looking at", never
/// as "a blank capture proves tampering".
/// </param>
/// <param name="RemoteOnly">
/// Someone is logged on, nobody is at the console, and at least one of those logons arrived over
/// RDP. Capture is expected to fail here and must not be counted against the machine.
/// </param>
public readonly record struct SessionSnapshot(
    bool AnyUserLoggedOn,
    bool ActiveConsoleUser,
    bool RemoteOnly);

/// <summary>
/// Answers "who, if anyone, is logged on to this machine right now" from the Terminal Services
/// session table, so the agent can tell an empty desk from a broken capture path.
///
/// Do not reach for <c>InteractiveProcessLauncher.HasActiveConsoleSession()</c> for this. That
/// probe asks <c>WTSGetActiveConsoleSessionId()</c> and treats only <c>0xFFFFFFFF</c> — a
/// transient state seen while the console is switching between sessions — as failure. A machine
/// sitting at the logon screen, or one whose user logged off ten hours ago, still has a perfectly
/// valid console session id, so that probe answers "yes, there is a desktop" all night. It is the
/// right question for the launcher, which only needs a session id to hand to
/// <c>WTSQueryUserToken</c>, and the wrong question here: acting on it would let the agent report
/// an unattended machine as one whose capture is being sabotaged.
///
/// Every rule below leans the same way. Where the session table is ambiguous the snapshot claims
/// less, because a machine wrongly reported as broken costs an employee an accusation, while a
/// missed report costs one polling interval.
///
/// This runs on the tracking loop's cadence for the service's whole lifetime, so every buffer
/// wtsapi32 hands out is released on its own <c>finally</c>; a per-poll leak would be invisible for
/// weeks and then fatal.
/// </summary>
public static class InteractiveSessions
{
    /// <summary>
    /// The snapshot returned whenever the session table cannot be read or believed. All-false reads
    /// as "we cannot tell", which suppresses fault reporting rather than inventing a fault.
    /// </summary>
    private static readonly SessionSnapshot Unknown = new(false, false, false);

    /// <summary>Returned by <c>WTSGetActiveConsoleSessionId</c> while no console is attached.</summary>
    private const uint NoConsoleSession = 0xFFFFFFFF;

    /// <summary>
    /// Session 0, which has hosted only services since Vista's session isolation and therefore can
    /// never hold a person, whatever name <c>WTSUserName</c> reports for it.
    /// </summary>
    private const uint ServicesSession = 0;

    /// <summary>WTS_CONNECTSTATE_CLASS.WTSActive — a user connected to their session.</summary>
    private const int WtsActive = 0;

    /// <summary>WTS_INFO_CLASS.WTSUserName.</summary>
    private const int InfoUserName = 5;

    /// <summary>WTS_INFO_CLASS.WTSClientProtocolType.</summary>
    private const int InfoClientProtocolType = 16;

    /// <summary>Protocol 0 is the local console; 1 is legacy ICA and 2 is RDP.</summary>
    private const ushort ProtocolConsole = 0;

    /// <summary>WTS_CURRENT_SERVER_HANDLE, i.e. this machine.</summary>
    private static readonly IntPtr CurrentServer = IntPtr.Zero;

    /// <summary>
    /// Reads the session table now. Never throws: anything unexpected yields the all-false snapshot,
    /// which reads as "we cannot tell" and suppresses fault reporting rather than inventing one.
    /// </summary>
    public static SessionSnapshot Current()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Unknown;
        }

        try
        {
            return QueryWindows();
        }
        catch (Exception)
        {
            // Deliberately unfiltered. This is a diagnostic probe called from a long-lived service
            // loop; a missing export, a hostile session-table state or a marshalling surprise must
            // degrade to silence, never take tracking down with it.
            return Unknown;
        }
    }

    /// <summary>The facts about one session that the verdict depends on, with no native types left.</summary>
    private readonly record struct SessionFacts(uint SessionId, bool IsActive, bool HasUser, bool IsRemote);

    /// <summary>
    /// The whole verdict, expressed as a pure function of the enumerated sessions and the console
    /// session id. Kept separate from the P/Invoke so the rule can be read and reasoned about
    /// without a Windows box in the loop.
    /// </summary>
    private static SessionSnapshot Summarise(IReadOnlyList<SessionFacts> sessions, uint consoleSessionId)
    {
        var anyUser = false;
        var activeConsoleUser = false;
        var remoteUser = false;

        foreach (var session in sessions)
        {
            // Session 0 is services only, and the listener sessions have no user name; neither is a
            // person, and counting either would make an idle machine look occupied.
            if (session.SessionId == ServicesSession || !session.HasUser)
            {
                continue;
            }

            anyUser = true;

            if (session.IsRemote)
            {
                remoteUser = true;
            }

            // Carrying the console session id is not enough. When a user connects over RDP to a
            // session that was local, that session keeps its id and can still be the one the
            // console id names, so the id alone would call an RDP desktop "the physical console"
            // and turn its unavoidable capture failures into a reported fault. The protocol is what
            // settles it, and an unreadable protocol counts as local so an unknown can never excuse
            // a genuine failure.
            if (session.IsActive
                && !session.IsRemote
                && consoleSessionId != NoConsoleSession
                && session.SessionId == consoleSessionId)
            {
                activeConsoleUser = true;
            }
        }

        return new SessionSnapshot(anyUser, activeConsoleUser, anyUser && !activeConsoleUser && remoteUser);
    }

    [SupportedOSPlatform("windows")]
    private static SessionSnapshot QueryWindows()
    {
        // The out pointer is only meaningful when the call succeeds; freeing it on the failure path
        // would hand wtsapi32 back whatever happened to be in that slot.
        if (!WTSEnumerateSessionsW(CurrentServer, 0, 1, out var table, out var count))
        {
            return Unknown;
        }

        try
        {
            if (table == IntPtr.Zero || count <= 0)
            {
                return Unknown;
            }

            var stride = Marshal.SizeOf<WTS_SESSION_INFO>();
            var facts = new List<SessionFacts>(count);
            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<WTS_SESSION_INFO>(IntPtr.Add(table, i * stride));
                var hasUser = QueryUserName(entry.SessionId).Length > 0;
                facts.Add(new SessionFacts(
                    entry.SessionId,
                    entry.State == WtsActive,
                    hasUser,
                    // Only a named session can move the verdict, so the unnamed ones do not get a
                    // second native round trip every poll for an answer nobody reads.
                    IsRemote: hasUser && IsRemote(entry.SessionId)));
            }

            return Summarise(facts, WTSGetActiveConsoleSessionId());
        }
        finally
        {
            if (table != IntPtr.Zero)
            {
                WTSFreeMemory(table);
            }
        }
    }

    /// <summary>
    /// The session's user name, or empty when it has none. Empty is the honest answer for a query
    /// that failed as well: an unnamed session is one we will not claim a person is sitting at.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string QueryUserName(uint sessionId)
    {
        if (!WTSQuerySessionInformationW(CurrentServer, sessionId, InfoUserName, out var buffer, out _)
            || buffer == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer)?.Trim() ?? string.Empty;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>
    /// True when the session is driven over RDP or ICA rather than the local console. Unknown
    /// counts as local, because the alternative is excusing a genuine capture failure as "they were
    /// only on RDP".
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool IsRemote(uint sessionId)
    {
        if (!WTSQuerySessionInformationW(CurrentServer, sessionId, InfoClientProtocolType, out var buffer, out var bytes)
            || buffer == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            // WTSClientProtocolType is a USHORT, not the DWORD the name suggests.
            return bytes >= sizeof(ushort) && unchecked((ushort)Marshal.ReadInt16(buffer)) != ProtocolConsole;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>
    /// WTS_SESSION_INFOW. <c>WinStationName</c> is declared as a raw pointer rather than a marshalled
    /// string because the whole block belongs to wtsapi32 until <c>WTSFreeMemory</c>; it is present
    /// only so the struct has the size the array stride is computed from.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    // ExactSpelling pins these to the W exports. Without it the default ANSI charset lets the
    // runtime fall back to probing for an "A"-suffixed name, which is not what the Unicode
    // buffers below are read as.
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(
        IntPtr server,
        uint reserved,
        uint version,
        out IntPtr sessionInfo,
        out int count);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr server,
        uint sessionId,
        int infoClass,
        out IntPtr buffer,
        out int bytesReturned);

    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WTSGetActiveConsoleSessionId();
}
