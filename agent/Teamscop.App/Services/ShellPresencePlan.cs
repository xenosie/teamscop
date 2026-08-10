namespace Teamscop.App.Services;

/// <summary>
/// Defect 2 — "if admin close the app, how to re-open it again? cannot find the item at any place."
///
/// The rule this encodes: <b>the app is never running, invisible and unreachable</b>. Whichever
/// branch start-up takes, at least one way back to the window has to exist, and which one it is
/// depends only on whether this machine has a sticker.
///
/// A monitored machine keeps the sticker on screen permanently, so the shell may hide on close and
/// a double-click brings it back (§4.6). An admin console has no sticker, so hiding would strand
/// the window behind nothing at all — there closing must genuinely exit the process, which is what
/// makes the desktop and Start-Menu shortcut a working way back in, and a tray icon is carried as
/// well so recovery does not depend on a .lnk surviving an installer that has already proved it can
/// lose one.
/// </summary>
public sealed record ShellPresencePlan(
    bool StickerRestoresShell,
    bool CloseExitsProcess,
    bool ShowTrayIcon)
{
    public static ShellPresencePlan For(bool stickerActive)
        => stickerActive
            ? new ShellPresencePlan(StickerRestoresShell: true, CloseExitsProcess: false, ShowTrayIcon: false)
            : new ShellPresencePlan(StickerRestoresShell: false, CloseExitsProcess: true, ShowTrayIcon: true);

    /// <summary>
    /// The invariant, stated so a test can hold it: every plan leaves a route back to the window.
    /// If this is ever false the app has become unreachable while still running, which is exactly
    /// the state the owner reported.
    /// </summary>
    public bool HasRecoveryPath => StickerRestoresShell || CloseExitsProcess || ShowTrayIcon;

    /// <summary>Avalonia only auto-shows an assigned MainWindow, which a sticker machine must not do.</summary>
    public bool AssignMainWindow => CloseExitsProcess;
}
