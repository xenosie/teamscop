using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Teamscop.App.Composition;
using Teamscop.App.Views;

namespace Teamscop.App.Services;

/// <summary>
/// Owns the always-on-top sticker and its relationship to the shell (§4.6). Replaces
/// StaffShellLifetime, whose AttachCloseToSticker built a fresh sticker window — and a fresh
/// DispatcherTimer — on every workspace close and never disposed the previous one.
///
/// Staff-role sessions get exactly one sticker, created at start-up without waiting on any HTTP
/// call, and exactly one shell that hides instead of closing. Admin sessions get neither.
/// </summary>
public sealed class StickerHost
{
    private readonly SessionStore _session;
    private readonly UiLog _log;
    private TimeTrackStickerWindow? _sticker;
    private Window? _shell;
    private bool _hasWorkspace;

    public StickerHost(SessionStore session, UiLog log)
    {
        _session = session;
        _log = log;
    }

    /// <summary>
    /// The machine is monitored and enrolled, so the sticker belongs on screen and the shell only
    /// hides. An unenrolled machine defaults to the staff store but holds no session — it must
    /// stay closable, or the login screen becomes a window nobody can quit.
    /// </summary>
    public bool IsActive => _session.IsStaffRole && _session.IsRegistered;

    /// <summary>
    /// §7.1 — plain staff (not leader, not policeman) can never open the main window. The shell is
    /// reachable only when this is not a monitored-with-session machine (the enrolment/admin case,
    /// where the shell is the whole UI) or the joined session actually has a workspace.
    /// </summary>
    public bool CanOpenShell => !IsActive || _hasWorkspace;

    /// <summary>
    /// Fed by <c>ShellViewModel.ApplyCapabilities</c> on every authority change, so a promotion to
    /// leader/policeman opens the gate mid-session and a demotion closes it — no restart.
    /// </summary>
    public void SetHasWorkspace(bool value) => _hasWorkspace = value;

    /// <summary>
    /// Binds the shell to the sticker: closing hides (§4.6), double-clicking the sticker brings
    /// it back. Nothing is created or destroyed on either transition. The handler re-reads
    /// <see cref="IsActive"/> at close time, so signing in mid-session takes effect immediately.
    /// </summary>
    public void Attach(Window shell)
    {
        _shell = shell;
        shell.Closing += (_, e) =>
        {
            if (!IsActive)
            {
                return;
            }

            e.Cancel = true;
            shell.Hide();
        };
    }

    /// <summary>
    /// Puts the sticker on screen. Cheap, synchronous and idempotent — no network, no role
    /// resolution — so it is safe to call at start-up and again after a staff sign-in.
    /// </summary>
    public void Show()
    {
        if (!IsActive)
        {
            return;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        if (_sticker is null)
        {
            _sticker = new TimeTrackStickerWindow(_session.State)
            {
                RequestOpenShell = ShowShell
            };
            _log.Info("Staff sticker started");
        }

        _sticker.Show();
    }

    /// <summary>
    /// Brings the shell back from the sticker, or from a promotion that just landed. This is the one
    /// chokepoint every open path funnels through — the sticker double-click, a second launch and
    /// the tray "Open" — so §7.1's gate lives here: a workspace-less monitored session can never
    /// open the shell, however the request arrived.
    /// </summary>
    public void ShowShell()
    {
        if (_shell is null || !CanOpenShell)
        {
            return;
        }

        _shell.Show();
        _shell.Activate();
    }

    /// <summary>Plain staff have no workspace, so the shell steps aside and leaves the sticker (§4.4).</summary>
    public void HideShell() => _shell?.Hide();
}
