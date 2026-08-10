using Avalonia.Controls;
using Avalonia.Platform;
using Teamscop.App.Composition;

namespace Teamscop.App.Services;

/// <summary>
/// Defect 2 — the in-product way back into the admin console. Closing the shell exits the process
/// (see <see cref="ShellPresencePlan"/>), and the shortcut is meant to relaunch it; the tray icon
/// is the belt for the case that already happened once, where the shortcut pointed into a directory
/// a normal desktop session could not execute from and there was no other entry point at all.
///
/// Everything here is best effort. A machine with no system tray (or a headless test host) simply
/// has no icon — it must never stop the app from starting.
/// </summary>
public sealed class TrayHost : IDisposable
{
    private const string IconAsset = "avares://Teamscop.App/Assets/teamscop-icon.png";

    private readonly UiLog _log;
    private TrayIcon? _icon;

    public TrayHost(UiLog log)
    {
        _log = log;
    }

    /// <summary>True when an icon is actually on screen — the caller may log the difference.</summary>
    public bool IsInstalled => _icon is not null;

    public void Install(Action open, Action exit)
    {
        if (_icon is not null)
        {
            return;
        }

        try
        {
            var openItem = new NativeMenuItem("Open Teamscop");
            openItem.Click += (_, _) => open();
            var exitItem = new NativeMenuItem("Exit");
            exitItem.Click += (_, _) => exit();

            var icon = new TrayIcon
            {
                ToolTipText = "Teamscop",
                Menu = new NativeMenu { Items = { openItem, exitItem } },
                IsVisible = true
            };

            // Left-click is the gesture people actually use; the menu is the discoverable one.
            icon.Clicked += (_, _) => open();

            try
            {
                icon.Icon = new WindowIcon(AssetLoader.Open(new Uri(IconAsset)));
            }
            catch (Exception ex)
            {
                // An icon-less tray entry is still a way back; a throw here would not be.
                _log.Debug($"Tray icon art unavailable: {ex.GetType().Name}");
            }

            _icon = icon;
        }
        catch (Exception ex)
        {
            _log.Debug($"System tray unavailable: {ex.GetType().Name}");
        }
    }

    public void Dispose()
    {
        try
        {
            if (_icon is not null)
            {
                _icon.IsVisible = false;
                _icon.Dispose();
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Tray icon could not be removed: {ex.GetType().Name}");
        }

        _icon = null;
    }
}
