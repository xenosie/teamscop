using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Avalonia;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.Setup;

/// <summary>
/// Windows-only by construction: every member below drives SCM, the registry or user32.
/// <see cref="Main"/> is the one platform-neutral entry point and refuses to go further
/// anywhere else. The class-level attribute is what tells the analyzer that, instead of
/// 36 CA1416 warnings restating it one call site at a time.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string ServiceName = "TeamscopStaff";
    private const string ServiceDisplay = "Teamscop Staff Agent";
    private const string ServiceDescription = "Teamscop background agent. Managed by company admin.";
    private const string ApiBaseUrl = "https://teamscop.com";

    [STAThread]
    [UnsupportedOSPlatform("browser")]
    private static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                // Not Fail() — MessageBoxW is a user32 P/Invoke and would throw here.
                Console.Error.WriteLine("Teamscop setup only runs on Windows.");
                return 1;
            }

            // §8.2 — silent, service-invoked maintenance entries. No UI, no prompts (they run in
            // session 0), and each handles its own errors so the MessageBox catch below is never hit.
            if (HasArg(args, "--demote-admin"))
            {
                return RunDemoteAdmin();
            }

            if (HasArg(args, "--ensure-staff-service"))
            {
                return RunEnsureStaffService();
            }

            if (!IsSupportedWindows())
            {
                Fail(
                    "Teamscop requires 64-bit Windows 10 or Windows 11.\n\n" +
                    "Windows 8 / 8.1 are not supported (.NET 8).\n" +
                    "Detected: " + DescribeOs() + ".");
                return 1;
            }

            if (HasArg(args, "/uninstall", "--uninstall", "/u"))
            {
                return RunUninstall();
            }

            if (HasArg(args, "/repair", "--repair"))
            {
                return RunRepair();
            }

            return RunInstall();
        }
        catch (Exception ex)
        {
            Fail("Teamscop setup failed:\n\n" + ex.Message);
            return 1;
        }
    }

    private static bool HasArg(string[] args, params string[] names)
        => args.Any(a => names.Any(n => string.Equals(a, n, StringComparison.OrdinalIgnoreCase)));

    private static int RunInstall()
    {
        // The dashboard is the install now: branded, step list, live progress — the product's own
        // look at the one moment every employee stares at it. If Avalonia cannot start (headless,
        // broken GPU stack), the same pipeline runs under the old message boxes instead: a plain
        // install always beats a pretty failure.
        try
        {
            Ui.InstallerApp.VersionLabel = ProductVersion();
            Ui.InstallerApp.Work = progress => RunInstallCore(progress);
            Ui.InstallerApp.OnFinished = LaunchInstalledApp;
            BuildInstallerAvaloniaApp().StartWithClassicDesktopLifetime([]);
            return Ui.InstallerApp.ResultExitCode;
        }
        catch (Exception ex)
        {
            UninstallAuditLine("Installer UI unavailable (" + ex.GetType().Name + "); falling back to dialogs.");
        }

        try
        {
            var code = RunInstallCore(progress: null);
            if (code == 0)
            {
                // The modal OK is this path's Finish button: the app opens only after the click.
                Ok("Teamscop installed successfully.\n\nClick OK to open Teamscop, " +
                   "then Register (admin) or Join with company token (staff).");
                LaunchInstalledApp();
            }

            return code;
        }
        catch (Exception ex)
        {
            Fail("Teamscop setup failed:\n\n" + ex.Message);
            return 1;
        }
    }

    private static Avalonia.AppBuilder BuildInstallerAvaloniaApp()
        => Avalonia.AppBuilder.Configure<Ui.InstallerApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// The install pipeline, UI-free. Reports through <paramref name="progress"/> when a dashboard
    /// is attached and runs identically without one. Throwing aborts the install; the caller
    /// renders the failure.
    /// </summary>
    private static int RunInstallCore(IProgress<Ui.InstallProgress>? progress)
    {
        void Report(int step, double percent, string detail)
            => progress?.Report(new Ui.InstallProgress(step, percent, detail));

        Report(0, 1, "Creating folders");
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Teamscop");
        var bin = Path.Combine(root, "bin");
        var agent = Path.Combine(root, "Agent");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(agent);

        // Write the ARP entry FIRST, before anything can throw, so a partial install is still
        // removable from Settings. The single writer of this key is WriteUninstallRegistry — nothing
        // re-asserts it — so a failure after this point used to leave a fully-deployed but
        // unremovable product (exactly the owner's situation after he hand-deleted the key).
        Report(0, 3, "Registering with Windows");
        WriteUninstallRegistry(root);

        // An install deliberately stops the service and replaces every binary, which for a few
        // minutes looks exactly like sabotage: service stopped, files briefly absent. Mark the
        // window so the desktop app stays quiet rather than reporting the whole fleet as broken
        // every time it is upgraded. Cleared at the end of a successful install.
        SetInstallInProgress(true);

        Report(1, 6, "Looking for older Teamscop versions");
        // A prior generation of this product installed to Program Files under its own service name.
        // Nothing in the current install or uninstall path knows those names, so that service kept
        // running across every reinstall — one was found alive a month later, holding file handles
        // and owning the capture pipe. Retire it before this build lays down its own.
        RemoveLegacyInstalls();

        // Disarm before stopping. The service carries SCM restart actions (5s/10s/30s), so a build
        // that crashes on startup is relaunched automatically — and a `sc stop` + kill only holds
        // the file free for a few seconds. Extraction of an 80MB payload takes longer than that, so
        // SCM would restart the old binary mid-extract and re-lock it, failing the very install that
        // was meant to replace the crashing build. Disabling the start type and clearing the failure
        // actions makes the stop stick; InstallService re-arms both below.
        Report(2, 12, "Stopping the Teamscop service");
        Sc("config", $"{ServiceName} start= disabled");
        Sc("failure", $"{ServiceName} reset= 0 actions= \"\"");

        // Stop AND kill: a reinstall over a running install collided with locked binaries inside
        // ExtractPayload and aborted (RunInstall used to only `sc stop`, never kill the app / helper
        // / usb sticker the way the uninstall path does).
        Sc("stop", ServiceName);
        Thread.Sleep(1200);
        Report(2, 18, "Closing Teamscop windows");
        KillRunningTeamscopProcesses();
        Thread.Sleep(600);

        // Copying dominates the wall clock, so it owns the widest slice of the bar (25 → 80).
        ExtractPayload(bin, (name, fraction) => Report(3, 25 + fraction * 55, name));
        Report(4, 82, "Writing configuration");
        WriteAgentConfig(bin);
        Report(4, 86, "Securing folders");
        HardenDirectoryAcls(root);
        Report(5, 90, "Registering the Windows service");
        InstallService(Path.Combine(bin, "Teamscop.StaffService.exe"));
        Report(5, 94, "Registering startup");
        RegisterRunAtLogon(bin);
        var app = Path.Combine(bin, "Teamscop.App.exe");
        Report(5, 96, "Creating shortcuts");
        CreateShortcuts(app, bin);
        // Re-write ARP now that InstallLocation and DisplayIcon paths exist on disk, filling in the
        // size/version fields that need the extracted payload.
        WriteUninstallRegistry(root);

        if (!File.Exists(app))
        {
            throw new InvalidOperationException("Teamscop.App.exe missing after extract.");
        }

        // Everything is on disk and the service is registered, so the upgrade window is over.
        Report(6, 98, "Cleaning up");
        SetInstallInProgress(false);

        // Deliberately NOT launched here. The Finish button is the go signal — the app opens when
        // the person says so, not while they are still reading the screen.
        s_installedAppPath = app;
        Report(6, 100, "Ready");
        return 0;
    }

    /// <summary>Set by a successful RunInstallCore; consumed by the Finish click (or the OK dialog).</summary>
    private static string? s_installedAppPath;

    private static void LaunchInstalledApp()
    {
        if (string.IsNullOrWhiteSpace(s_installedAppPath) || !File.Exists(s_installedAppPath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = s_installedAppPath,
                WorkingDirectory = Path.GetDirectoryName(s_installedAppPath),
                UseShellExecute = true
            });
        }
        catch
        {
            // The install itself is complete; the shortcut still opens the app.
        }
    }

    /// <summary>
    /// Clean uninstall (fail-closed):
    /// 1) confirm  2) authorize (staff TOTP)  3) only then stop processes + delete product bits.
    /// Cancel / Ctrl+C / bad TOTP → abort with install left intact.
    /// Preserves %ProgramData%\Teamscop\Agent (vault/outbox accumulation).
    /// </summary>
    private static int RunUninstall()
    {
        if (!AskYesNo(
                "Uninstall Teamscop?\n\n" +
                "Removes the app, service, shortcuts, and Start Menu entry.\n" +
                "Tracking data under ProgramData\\Teamscop\\Agent is kept."))
        {
            return 0;
        }

        // Authorize BEFORE stopping/deleting anything. Cancel must leave the product intact.
        if (!AuthorizeUninstallOrAbort())
        {
            Fail(
                "Uninstall cancelled or authorization failed.\n\n" +
                "Nothing was removed. Teamscop is still installed.");
            return 2;
        }

        // The point of commitment: authorization has passed and nothing has been torn down yet.
        // Recording here — rather than when the code was accepted — means the app-history entry
        // exists if and only if the product is actually being removed.
        RecordUninstallForAudit();

        StopTeamscopCompletely();
        RemoveProductBits();
        Ok(
            "Teamscop uninstalled.\n\n" +
            "Removed: app, service, shortcuts, autostart.\n" +
            "Kept: ProgramData\\Teamscop\\Agent (local tracking data).");
        return 0;
    }

    /// <summary>
    /// Reset a broken install without hand-running registry commands: stop everything, lift the USB
    /// gate, clear markers, remove product binaries. Gated exactly like uninstall — a repair still
    /// tears down the product, so it must not be a free bypass. Unlike uninstall it tolerates locked
    /// files rather than failing at the end: the whole point is to recover a wedged machine.
    /// </summary>
    private static int RunRepair()
    {
        if (!AskYesNo(
                "Repair / reset Teamscop?\n\n" +
                "Stops the service and all Teamscop processes, lifts the USB block, clears markers, "
                + "and removes the program files so you can reinstall cleanly.\n\n"
                + "Tracking data under ProgramData\\Teamscop\\Agent is kept."))
        {
            return 0;
        }

        if (!AuthorizeUninstallOrAbort())
        {
            Fail(
                "Repair cancelled or authorization failed.\n\n" +
                "Nothing was changed. Teamscop is still installed.");
            return 2;
        }

        UninstallAuditLine("Repair/reset invoked.");
        StopTeamscopCompletely();
        try
        {
            RemoveProductBits();
        }
        catch (Exception ex)
        {
            // Repair is best effort by design: report what is left rather than abort.
            Fail(
                "Repair completed with warnings — some files were locked and could not be removed:\n\n"
                + ex.Message + "\n\nReboot and run repair again if needed.");
            return 0;
        }

        Ok(
            "Teamscop reset.\n\n" +
            "Stopped everything, lifted the USB block, cleared markers and removed program files.\n" +
            "Kept: ProgramData\\Teamscop\\Agent. You can reinstall now.");
        return 0;
    }

    /// <summary>
    /// §8.2 — the service self-removal surgery, run as a detached elevated process the staff worker
    /// spawns once the machine's stored role is admin. Silent (session 0): it disables and deletes
    /// the service, drops the SessionHelper autostart, restores USB, and clears the StaffAgent marker
    /// so the leftover app is uninstallable with NO code (§1.5, §8 — an admin PC is not monitored).
    /// It deliberately KEEPS the app, the TeamscopApp Run key, the ARP entry and the UninstallGuard
    /// binary; InstalledRole stays "admin". Never kills its own parent (the service exits via
    /// StopApplication), and clears SCM recovery BEFORE stopping so nothing restarts it.
    /// </summary>
    private static int RunDemoteAdmin()
    {
        try
        {
            UninstallAuditLine("Admin machine detected — self-removing the staff service (--demote-admin).");

            // 1) Make sure the service can never come back: disable start and clear the recovery
            //    actions BEFORE the stop, so the service exiting is not turned into a restart.
            Sc("config", $"{ServiceName} start= disabled");
            Sc("failure", $"{ServiceName} reset= 0 actions= \"\"");
            Sc("failureflag", $"{ServiceName} 0");

            // 2) Stop and delete. Do NOT kill the StaffService tree here — this remover is a child of
            //    it, so that would kill this process mid-surgery. The service exits on its own via
            //    StopApplication; `sc delete` completes when that last handle closes.
            Sc("stop", ServiceName);
            Thread.Sleep(1500);
            KillProcessTree("Teamscop.SessionHelper");
            Sc("delete", ServiceName);

            // 3) Drop the SessionHelper autostart. The app's own Run key stays — the admin still uses
            //    the desktop app; it is only the capture service that goes.
            using (var run = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
            {
                run?.DeleteValue("TeamscopSessionHelper", throwOnMissingValue: false);
            }

            // 4) Give USB back — this machine may have briefly gated storage while it looked like staff.
            RestoreMachineViaGuard(Path.Combine(InstallRootOrDefault(), "bin"));
            RemoveUsbStoragePolicy();

            // 5) No longer a monitored machine: clear the staff marker so the leftover app can be
            //    removed from ARP with no code. Role stays "admin" so the capture gate stays shut too.
            using (var marker = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Teamscop", writable: true))
            {
                marker?.SetValue("StaffAgent", 0, RegistryValueKind.DWord);
            }

            UninstallAuditLine("Staff service removed; admin machine is no longer monitored.");
            return 0;
        }
        catch (Exception ex)
        {
            UninstallAuditLine("--demote-admin failed: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// §8.2 (admin→staff flip) — reinstall ONLY the service for a machine whose role changed back to
    /// staff after the service had removed itself. Everything else (app, ARP, shortcuts) is already
    /// present. Silent; invoked by the desktop app when it stamps the staff role and finds no service.
    /// </summary>
    private static int RunEnsureStaffService()
    {
        try
        {
            var bin = Path.Combine(InstallRootOrDefault(), "bin");
            var serviceExe = Path.Combine(bin, "Teamscop.StaffService.exe");
            if (!File.Exists(serviceExe))
            {
                UninstallAuditLine("--ensure-staff-service: service exe missing at " + serviceExe);
                return 1;
            }

            using (var marker = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Teamscop", writable: true))
            {
                marker?.SetValue("StaffAgent", 1, RegistryValueKind.DWord);
            }

            InstallService(serviceExe);
            UninstallAuditLine("Staff service reinstalled (--ensure-staff-service).");
            return 0;
        }
        catch (Exception ex)
        {
            UninstallAuditLine("--ensure-staff-service failed: " + ex.Message);
            return 1;
        }
    }

    private static string InstallRootOrDefault()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Teamscop");
            if (key?.GetValue("InstallRoot") is string root && !string.IsNullOrWhiteSpace(root))
            {
                return root;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Fall through to the default.
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Teamscop");
    }

    private static bool AuthorizeUninstallOrAbort()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Teamscop");
        var bin = Path.Combine(root, "bin");
        var guard = Path.Combine(bin, "Teamscop.UninstallGuard.exe");

        // Staff detection must NOT use agent-state.json — that file is owned by the
        // monitored user (asInvoker App) and can be deleted to skip the gate.
        // Key off SCM service presence and/or HKLM marker (admin-writable only).
        if (!IsStaffMachineInstalled())
        {
            // Admin-only / no staff agent install marker: confirm already done.
            return true;
        }

        // A gate that can never be satisfied is not tamper resistance — it is an unremovable product,
        // which is a defect (§11.5 accepts that a local admin wins; §2.1 says employees ARE local
        // admins). §6 makes the uninstall code DERIVED from the machine's device key, so any machine
        // that has joined a company can verify one. But a machine with NO device key (never joined)
        // has no code that could exist, so requiring one is impossible, not secure. Fall back to the
        // elevated-admin confirmation already taken above, and log it. This requires local
        // administrator (setup is requireAdministrator) and applies only while the machine has no
        // device key; the moment it joins, a code is derivable and is required again.
        if (!CanVerifyUninstallCode())
        {
            UninstallAuditLine(
                "Uninstall authorized WITHOUT a code: this machine has no device key (never joined a "
                + "company), so no code could exist. Proceeding on the elevated-admin confirmation.");
            return true;
        }

        // Staff machine WITH a provisioned secret: missing guard fails closed (do not skip TOTP).
        if (!File.Exists(guard))
        {
            return false;
        }

        // Staff machines: TOTP required. Cancel/Ctrl+C/non-zero → abort uninstall (fail closed).
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = guard,
                WorkingDirectory = bin,
                UseShellExecute = true // sticker form (WinExe)
            });
            if (p is null)
            {
                return false;
            }

            if (!p.WaitForExit(300_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return false;
            }

            // §11.2 — the guard verifies the code against the secret sealed on this machine, so
            // uninstall works with no network. There is no ticket to consume: the previous flow
            // POSTed to /api/lifecycle/uninstall/{verify,consume} and failed closed offline, which
            // made a disconnected machine impossible to uninstall at all.
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when this machine can verify an uninstall code — §6 derives it from the device key, so
    /// the question is only "does this machine have a device key". A machine that never joined a
    /// company has none, so no code could ever exist and the guard must not be an unremovable wall.
    /// </summary>
    /// <summary>
    /// Tells the server the product was removed with authorization, before anything is torn down.
    /// Best effort and time-boxed: an uninstall must not hang on a dead network.
    /// </summary>
    private static void RecordUninstallForAudit()
    {
        var guard = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Teamscop", "bin", "Teamscop.UninstallGuard.exe");
        if (!File.Exists(guard))
        {
            return;
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = guard,
                Arguments = "--record-uninstall",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p?.WaitForExit(15_000);
        }
        catch (Exception ex)
        {
            UninstallAuditLine("Could not record the uninstall event: " + ex.Message);
        }
    }

    /// <summary>
    /// Whether this machine has a device key, and therefore whether a code can exist to check.
    ///
    /// HKLM is consulted first and the session file only as a fallback. The session file lives in a
    /// directory granted BUILTIN\Users:Modify so the monitored employee can write their own state —
    /// which meant deleting one file they owned made this return false and opened the uninstall gate
    /// with no code at all, while the service and the HKLM marker still said "staff machine". The
    /// service mirrors the key into HKLM, which an unelevated user cannot touch.
    /// </summary>
    private static bool CanVerifyUninstallCode()
    {
        if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(ReadHklmDeviceKey()))
        {
            return true;
        }

        try
        {
            return !string.IsNullOrWhiteSpace(new LocalAgentStore(AgentRole.Staff).Load().DeviceKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // If we cannot even look, fail closed toward requiring the code.
            return true;
        }
    }

    /// <summary>
    /// Marks (or clears) the window during which this machine is being installed or upgraded.
    ///
    /// The desktop app reads it and suppresses its status report while it is set. Without it, every
    /// routine upgrade would report a stopped service and missing binaries from every machine at
    /// once — a fleet-wide false accusation on a completely normal operation.
    /// </summary>
    private static void SetInstallInProgress(bool value)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Teamscop", writable: true);
            if (key is null)
            {
                return;
            }

            if (value)
            {
                key.SetValue("InstallInProgress", 1, RegistryValueKind.DWord);
            }
            else
            {
                key.DeleteValue("InstallInProgress", throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Non-fatal. Worst case the app reports a defect it should have suppressed, and the
            // server's debounce absorbs most of it.
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadHklmDeviceKey()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Teamscop");
            return (key?.GetValue("DeviceKey") as string)?.Trim();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>Best-effort audit breadcrumb under the install root; never fatal.</summary>
    private static void UninstallAuditLine(string message)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Teamscop");
            Directory.CreateDirectory(root);
            File.AppendAllText(
                Path.Combine(root, "uninstall.log"),
                $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Audit is best effort — its absence must not block or fail an uninstall.
        }
    }

    /// <summary>
    /// True when this machine has a staff agent install signal the monitored user cannot forge away.
    /// </summary>
    private static bool IsStaffMachineInstalled()
    {
        // sc query returns 0 when the service exists in any state; 1060 when not found.
        if (Sc("query", ServiceName) == 0)
        {
            return true;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Teamscop");
            var value = key?.GetValue("StaffAgent");
            if (value is int i && i != 0)
            {
                return true;
            }

            if (value is string s &&
                (string.Equals(s, "1", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        catch
        {
            // treat as not present
        }

        return false;
    }

    private static readonly string[] TeamscopProcessNames =
    {
        "Teamscop.App",
        "Teamscop.SessionHelper",
        "Teamscop.StaffService",
        "Teamscop.UsbApproval",
        "Teamscop.UninstallGuard"
    };

    /// <summary>
    /// Stops and deregisters services left behind by older generations of Teamscop that installed
    /// somewhere other than ProgramData\Teamscop.
    ///
    /// Identified by where the service's image actually lives, not by name — the whole reason the
    /// leftover survived is that nothing here knew what it was called. Only services whose binary
    /// sits under Program Files\Teamscop are touched, and the current service is skipped explicitly.
    ///
    /// Files are deliberately left on disk. Deregistering the service is what stops it running, and
    /// deleting from Program Files during someone else's install is a bigger promise than this needs
    /// to make.
    /// </summary>
    private static void RemoveLegacyInstalls()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var legacyRoot = Path.Combine(Environment.GetFolderPath(folder), "Teamscop");
            try
            {
                using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
                if (services is null)
                {
                    return;
                }

                foreach (var name in services.GetSubKeyNames())
                {
                    if (string.Equals(name, ServiceName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        using var key = services.OpenSubKey(name);
                        if (key?.GetValue("ImagePath") is not string image
                            || image.IndexOf(legacyRoot, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        Sc("config", $"{name} start= disabled");
                        Sc("stop", name);
                        Sc("delete", name);
                    }
                    catch
                    {
                        // Unreadable key or a service we may not touch; the prefix kill still applies.
                    }
                }
            }
            catch
            {
                // Service enumeration is best effort — never block an install on it.
            }
        }
    }

    /// <summary>
    /// Kill every Teamscop process except this setup, without deleting the service.
    ///
    /// Matched by prefix, not by an exact list. The list only knew today's five executable names, so
    /// a process from an older version of the product — "teamscop-service" was found still running a
    /// month after it was replaced — survived every reinstall, kept file handles open in bin\, and
    /// held the capture pipe name. A prefix match costs nothing and covers names this build has
    /// never heard of, which is exactly the case the list could not handle.
    /// </summary>
    private static void KillRunningTeamscopProcesses()
    {
        foreach (var name in TeamscopProcessNames)
        {
            KillProcessTree(name);
        }

        try
        {
            foreach (var p in Process.GetProcesses())
            {
                if (p.Id == Environment.ProcessId
                    || !p.ProcessName.StartsWith("teamscop", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
                catch
                {
                    // Already gone, or protected. The extract below reports what it cannot replace.
                }
            }
        }
        catch
        {
            // Enumerating processes can fail under tight policy; the named kills above still ran.
        }
    }

    private static void StopTeamscopCompletely()
    {
        Sc("stop", ServiceName);
        Thread.Sleep(800);

        KillRunningTeamscopProcesses();

        Thread.Sleep(1200);
        Sc("stop", ServiceName);
        Sc("delete", ServiceName);
        Thread.Sleep(500);
    }

    private static void KillProcessTree(string processName)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(processName))
            {
                try
                {
                    // Don't kill our own setup process mid-uninstall.
                    if (p.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Removes the product. Only ever reached from <see cref="RunUninstall"/>, i.e. after
    /// <see cref="AuthorizeUninstallOrAbort"/> has passed (§11.3 — there is no unauthorized
    /// removal path). Captured data under ProgramData\Teamscop\Agent is always preserved.
    /// </summary>
    /// <summary>
    /// Runs the guard's cleanup half. The guard owns this because it links Engine.Usb, which knows
    /// how to re-enable the disabled device nodes; the setup executable is deliberately standalone.
    /// Never fatal — a failure here must not strand a half-removed product on the machine.
    /// </summary>
    private static void RestoreMachineViaGuard(string bin)
    {
        var guard = Path.Combine(bin, "Teamscop.UninstallGuard.exe");
        if (!File.Exists(guard))
        {
            return;
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = guard,
                Arguments = "--restore-machine",
                WorkingDirectory = bin,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p?.WaitForExit(60_000);
        }
        catch
        {
            // Best effort; the policy key removal below still brings USB back.
        }
    }

    private static void RemoveProductBits()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Teamscop");
        var bin = Path.Combine(root, "bin");

        // Undo the USB gate and destroy the offline approval secret — but only here, where removal
        // is actually happening. The guard used to do this the moment a code was accepted, so one
        // legitimately issued uninstall code permanently unblocked USB even if the user then
        // cancelled and the agent kept running (§9.4). Best effort: a machine that was never gated
        // has nothing to undo, and the policy key is deleted below regardless.
        RestoreMachineViaGuard(bin);

        using (var run = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
        {
            run?.DeleteValue("TeamscopApp", throwOnMissingValue: false);
            run?.DeleteValue("TeamscopSessionHelper", throwOnMissingValue: false);
        }

        // Belt for a machine that still carries the pre-fix HKCU autostart values.
        using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
        {
            run?.DeleteValue("TeamscopApp", throwOnMissingValue: false);
            run?.DeleteValue("TeamscopSessionHelper", throwOnMissingValue: false);
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        TryDelete(Path.Combine(desktop, "Teamscop.lnk"));
        TryDelete(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            "Programs", "Teamscop", "Teamscop.lnk"));
        TryDeleteDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            "Programs", "Teamscop"));
        TryDelete(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs", "Teamscop", "Teamscop.lnk"));
        TryDeleteDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs", "Teamscop"));

        using (var arp = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", writable: true))
        {
            try { arp?.DeleteSubKeyTree("Teamscop", throwOnMissingSubKey: false); } catch { /* ignore */ }
            try { arp?.DeleteSubKeyTree("TeamscopStaffAgent", throwOnMissingSubKey: false); } catch { /* ignore */ }
        }

        try
        {
            using var hklm = Registry.LocalMachine.OpenSubKey("SOFTWARE", writable: true);
            hklm?.DeleteSubKeyTree("Teamscop", throwOnMissingSubKey: false);
        }
        catch
        {
            // ignore
        }

        RemoveUsbStoragePolicy();

        // Product binaries only — never wipe Agent vault/outbox.
        for (var attempt = 0; attempt < 4 && Directory.Exists(bin); attempt++)
        {
            if (attempt > 0)
            {
                StopTeamscopCompletely();
                Thread.Sleep(1000 * attempt);
            }

            TryDeleteDirectory(bin);
        }

        TryDelete(Path.Combine(root, "Teamscop_setup.exe"));

        // Remove root if empty (Agent may remain).
        try
        {
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
            {
                Directory.Delete(root);
            }
        }
        catch
        {
            // ignore
        }

        if (Directory.Exists(bin))
        {
            throw new InvalidOperationException(
                "Could not delete ProgramData\\Teamscop\\bin (still locked).\n" +
                "Reboot, then run the uninstall again as Administrator.");
        }
    }

    /// <summary>
    /// The USB block is machine-wide Group Policy, not product state: leaving it behind would mean
    /// removing Teamscop permanently kills USB storage on that PC. UninstallGuard also re-enables
    /// the individual device nodes it disabled; this is the belt for a machine where the guard did
    /// not run (admin-only install, or a guard that failed after authorization).
    /// </summary>
    private static void RemoveUsbStoragePolicy()
    {
        const string removableStorage = @"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(removableStorage, writable: true);
            key?.DeleteSubKeyTree("{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}", throwOnMissingSubKey: false);
        }
        catch
        {
            // ignore
        }
    }

    private static void ExtractPayload(string bin, Action<string, double>? onFile = null)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded payload.zip missing. Rebuild with deploy/windows/build-setup.sh");

        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Cannot open embedded payload.");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var total = Math.Max(1, zip.Entries.Count);
        var done = 0;
        foreach (var entry in zip.Entries)
        {
            onFile?.Invoke(entry.Name, (double)done++ / total);
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var dest = Path.Combine(bin, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            ExtractOneWithRetry(entry, dest);
        }
    }

    /// <summary>
    /// One payload file, with a short retry against transient locks and a hard, NAMED failure.
    ///
    /// A bare ExtractToFile throw here produced the worst upgrade outcome we have seen in the field:
    /// entries extract alphabetically, so Teamscop.App.* had already been replaced when a lingering
    /// process held Teamscop.StaffService.exe — leaving a machine with a NEW app and the OLD,
    /// crash-looping service, which then looked exactly like "the fix didn't work". The retry rides
    /// out SCM releasing handles; if the file is still locked after that, the install must fail
    /// loudly and name the file, because a partial bin\ is worse than no upgrade at all.
    /// </summary>
    private static void ExtractOneWithRetry(ZipArchiveEntry entry, string dest)
    {
        const int attempts = 5;
        for (var i = 1; ; i++)
        {
            try
            {
                entry.ExtractToFile(dest, overwrite: true);
                return;
            }
            catch (IOException) when (i < attempts)
            {
                Thread.Sleep(600);
            }
            catch (UnauthorizedAccessException) when (i < attempts)
            {
                Thread.Sleep(600);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Could not replace {Path.GetFileName(dest)} — a running process is holding it.\n\n" +
                    "Close Teamscop windows (or reboot) and run this installer again.\n" +
                    "Do not use the machine until the install completes: it is now partially upgraded.",
                    ex);
            }
        }
    }

    private static void WriteAgentConfig(string bin)
    {
        var path = Path.Combine(bin, "appsettings.Production.json");
        // Payload ships CompanyTokenKey; do not strip it (service refuses all-zero key).
        var companyKey = TryReadJsonString(path, "CompanyTokenKey");

        var json =
            "{\n" +
            "  \"Agent\": {\n" +
            $"    \"ApiBaseUrl\": \"{ApiBaseUrl}\",\n" +
            "    \"SyncSeconds\": 30";
        if (!string.IsNullOrWhiteSpace(companyKey))
        {
            json += ",\n" +
                    $"    \"CompanyTokenKey\": \"{EscapeJson(companyKey)}\"";
        }

        json += "\n  }\n}\n";
        File.WriteAllText(path, json);
    }

    private static string? TryReadJsonString(string path, string propertyName)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            var marker = "\"" + propertyName + "\"";
            var i = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0)
            {
                return null;
            }

            var colon = json.IndexOf(':', i);
            var q1 = json.IndexOf('"', colon + 1);
            var q2 = json.IndexOf('"', q1 + 1);
            if (q1 < 0 || q2 <= q1)
            {
                return null;
            }

            return json[(q1 + 1)..q2];
        }
        catch
        {
            return null;
        }
    }

    private static string EscapeJson(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static void InstallService(string serviceExe)
    {
        if (!File.Exists(serviceExe))
        {
            throw new InvalidOperationException("Teamscop.StaffService.exe missing in package.");
        }

        var quoted = "\"" + serviceExe + "\"";
        // create or reconfigure
        var create = Sc("create", $"{ServiceName} binPath= {quoted} start= auto DisplayName= \"{ServiceDisplay}\"");
        if (create != 0)
        {
            // Anything other than a fresh create — including 1073 "already exists" — must be
            // reconciled explicitly. RunInstall disables the start type before extracting so SCM
            // cannot resurrect a crashing build mid-copy; on an upgrade `create` returns 1073, so
            // treating that as "nothing to do" would leave the service permanently disabled and the
            // machine silently untracked. This line is what puts start= auto back.
            Sc("config", $"{ServiceName} binPath= {quoted} start= auto DisplayName= \"{ServiceDisplay}\"");
        }

        Sc("description", $"{ServiceName} \"{ServiceDescription}\"");

        // §11.4 — the service restarts itself when stopped or killed.
        // reset= 86400 : the failure counter goes back to zero after a quiet day, so a
        //                machine that misbehaves once a month never exhausts its actions.
        // actions=     : 1st failure 5s, 2nd 10s, 3rd-and-after 30s. The third entry is
        //                what SCM reuses for every subsequent failure, so a permanently
        //                broken build retries twice a minute instead of hammering the box.
        // failureflag= : without this SCM only reacts to a crash. Task Manager "End task"
        //                and `net stop` exit cleanly with a non-zero code, which is exactly
        //                the casual-removal case §11.5 targets.
        Sc("failure", $"{ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000");
        Sc("failureflag", $"{ServiceName} 1");

        // First start after create can race with file locks / AV; retry 1053 once.
        var start = Sc("start", ServiceName);
        if (start == 1053)
        {
            Thread.Sleep(2500);
            start = Sc("start", ServiceName);
        }

        if (start != 0 && start != 1056)
        {
            // 1056 = already running
            throw new InvalidOperationException(
                $"Could not start service {ServiceName} (sc exit {start}).\n\n" +
                "If this is 1053: open Event Viewer → Windows Logs → Application " +
                "and look for Teamscop.StaffService errors, then run setup again as Administrator.");
        }
    }

    private static void RegisterRunAtLogon(string bin)
    {
        var app = Path.Combine(bin, "Teamscop.App.exe");
        // HKLM Run, not HKCU: the installing account's HKCU is not necessarily the monitored user's
        // (setup is requireAdministrator, so over-the-shoulder elevation writes to the wrong hive),
        // and it only fires at that account's next logon. HKLM Run launches for EVERY interactive
        // user, which is what a machine-wide agent needs. With the ACL fix, bin is Users-readable so
        // a non-elevated logon can actually execute it.
        //
        // The SessionHelper is deliberately NOT registered here any more: the service owns its
        // lifetime (SessionHelperSupervisor) and launches it into the active session with the
        // logged-on user's token. A Run key could never launch it out of the locked bin directory.
        using var run = Registry.LocalMachine.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")
            ?? throw new InvalidOperationException("Cannot write HKLM Run key.");
        run.SetValue("TeamscopApp", "\"" + app + "\"");

        // Clean up any stale HKCU values a previous build wrote, so the app is not launched twice
        // and the dead helper key does not linger.
        using var hkcu = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        hkcu?.DeleteValue("TeamscopApp", throwOnMissingValue: false);
        hkcu?.DeleteValue("TeamscopSessionHelper", throwOnMissingValue: false);
    }

    private static void CreateShortcuts(string target, string workDir)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        WriteShortcut(Path.Combine(desktop, "Teamscop.lnk"), target, workDir);

        // All-users Start Menu (preferred) + current-user Start Menu fallback
        var commonPrograms = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            "Programs", "Teamscop");
        var userPrograms = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs", "Teamscop");
        try { Directory.CreateDirectory(commonPrograms); } catch { /* ignore */ }
        try { Directory.CreateDirectory(userPrograms); } catch { /* ignore */ }
        WriteShortcut(Path.Combine(commonPrograms, "Teamscop.lnk"), target, workDir);
        WriteShortcut(Path.Combine(userPrograms, "Teamscop.lnk"), target, workDir);

        // A second, independent way to reach the uninstaller.
        //
        // Removal has until now depended entirely on the Add/Remove Programs key. If that key is
        // missing — and on two machines it was — the product is undeletable through any normal route,
        // which is a far worse failure than an untidy Start Menu. This shortcut runs the same
        // "/uninstall" entry point, so the TOTP code prompt still gates it exactly as before.
        var uninstaller = Path.Combine(Path.GetDirectoryName(workDir) ?? workDir, "Teamscop_setup.exe");
        if (File.Exists(uninstaller))
        {
            WriteShortcut(
                Path.Combine(commonPrograms, "Uninstall Teamscop.lnk"),
                uninstaller,
                workDir,
                "/uninstall",
                "Uninstall Teamscop");
            WriteShortcut(
                Path.Combine(userPrograms, "Uninstall Teamscop.lnk"),
                uninstaller,
                workDir,
                "/uninstall",
                "Uninstall Teamscop");
        }
    }

    private static void WriteShortcut(
        string lnkPath,
        string target,
        string workDir,
        string? arguments = null,
        string? description = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(lnkPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            var shortcut = shell.CreateShortcut(lnkPath);
            shortcut.TargetPath = target;
            shortcut.WorkingDirectory = workDir;
            shortcut.Description = description ?? "Teamscop";
            shortcut.IconLocation = target + ",0";
            if (!string.IsNullOrEmpty(arguments))
            {
                shortcut.Arguments = arguments;
            }

            shortcut.Save();
        }
        catch
        {
            // Non-fatal
        }
    }

    private static void WriteUninstallRegistry(string root)
    {
        var setup = Environment.ProcessPath
            ?? Path.Combine(root, "bin", "Teamscop_setup.exe");
        // Copy setup next to install for ARP uninstall
        var setupCopy = Path.Combine(root, "Teamscop_setup.exe");
        try
        {
            if (!string.Equals(setup, setupCopy, StringComparison.OrdinalIgnoreCase)
                && File.Exists(setup))
            {
                File.Copy(setup, setupCopy, overwrite: true);
            }
        }
        catch
        {
            setupCopy = setup;
        }

        using var key = Registry.LocalMachine.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Teamscop")
            ?? throw new InvalidOperationException("Cannot write Uninstall registry key.");
        key.SetValue("DisplayName", "Teamscop");
        key.SetValue("DisplayVersion", ProductVersion());
        key.SetValue("Publisher", "Teamscop");
        key.SetValue("InstallLocation", root);
        key.SetValue("DisplayIcon", Path.Combine(root, "bin", "Teamscop.App.exe"));
        key.SetValue("UninstallString", "\"" + setupCopy + "\" /uninstall");
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        var sizeKb = EstimatedSizeKb(Path.Combine(root, "bin"));
        if (sizeKb > 0)
        {
            key.SetValue("EstimatedSize", sizeKb, RegistryValueKind.DWord);
        }

        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

        // Clear the values that hide an entry from Settings and Control Panel.
        //
        // An earlier generation of this installer set SystemComponent=1, which makes Windows omit the
        // product from "Installed apps" entirely. CreateSubKey opens the existing key and writes only
        // the values named above, so that flag survived every reinstall — the product was installed,
        // fully functional, and had no uninstall entry anywhere in the UI. Employees could not remove
        // it, which defeats the code-gated uninstall this key exists to offer.
        // NoRemove is the one that greys out the Uninstall button. Same stale-value story as
        // SystemComponent: an older build set it, CreateSubKey never clears what it does not write,
        // and it survived every reinstall — so the entry was visible but could not be acted on.
        foreach (var hidden in new[]
                 { "SystemComponent", "NoRemove", "ParentKeyName", "ParentDisplayName", "ReleaseType" })
        {
            try { key.DeleteValue(hidden, throwOnMissingValue: false); } catch { /* ignore */ }
        }

        // HKLM marker for staff uninstall gate — monitored users cannot delete this without admin.
        using var marker = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Teamscop")
            ?? throw new InvalidOperationException("Cannot write Teamscop HKLM install marker.");
        marker.SetValue("StaffAgent", 1, RegistryValueKind.DWord);
        marker.SetValue("InstallRoot", root, RegistryValueKind.String);

        // §7 — the exact moment this install (or upgrade) happened, plus which build it was. The
        // service reads these on its next loop and reports one "install" event carrying THIS
        // timestamp, so the admin's app history shows when the product landed on the machine —
        // not when the service eventually mentioned it.
        marker.SetValue("InstalledAtUtc", DateTimeOffset.UtcNow.ToString("O"), RegistryValueKind.String);
        marker.SetValue("InstallStamp", ProductVersion(), RegistryValueKind.String);

        // Record the machine's role so the capture gate and the uninstall gate can key off an
        // admin-writable value rather than agent-state.json. The desktop app stamps "admin"/"staff"
        // at registration; the installer seeds "unassigned" and — since this build — RESETS it.
        //
        // Preserving the old value looked polite and was a trap: one admin sign-in on an employee's
        // PC (the owner checking the roster on someone's machine) stamps it "admin", the service
        // then removes itself on every start, and no amount of reinstalling ever fixed it because
        // the reinstall preserved the poisoned marker. One machine was dead for a full day this way.
        // Running the installer is an explicit human act; the next sign-in restamps the truth, and
        // a genuine admin PC simply self-removes again exactly as designed.
        marker.SetValue("InstalledRole", "unassigned", RegistryValueKind.String);
    }

    private static string ProductVersion()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // Strip any "+<git sha>" build-metadata suffix Windows would not parse.
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }

        return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
    }

    private static int EstimatedSizeKb(string bin)
    {
        try
        {
            if (!Directory.Exists(bin))
            {
                return 0;
            }

            var bytes = Directory.EnumerateFiles(bin, "*", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            return (int)Math.Clamp(bytes / 1024, 0, int.MaxValue);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Absolute path to a Windows-supplied tool (B14). A bare file name resolves from the
    /// calling executable's directory first, and this setup runs elevated from whatever
    /// folder the employee downloaded it into — a planted sc.exe or icacls.exe there would
    /// sit directly on the uninstall authorization path.
    /// </summary>
    private static string SystemExe(string fileName)
    {
        var systemDir = Environment.SystemDirectory;
        return string.IsNullOrWhiteSpace(systemDir)
            ? fileName
            : Path.Combine(systemDir, fileName);
    }

    private static int Sc(string verb, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = SystemExe("sc.exe"),
            Arguments = verb + " " + args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start sc.exe");
        p.WaitForExit(60_000);
        return p.ExitCode;
    }

    /// <summary>
    /// Lock down ProgramData\Teamscop with the Program Files model.
    ///
    /// Only <c>Agent</c> (vault, outbox, state, secrets) stays SYSTEM+Administrators-only — that is
    /// the data §12.1 requires hiding from the employee. <c>root</c> and <c>bin</c> ALSO grant
    /// BUILTIN\Users read+execute, because a standard user has to LAUNCH the app and the service has
    /// to launch the SessionHelper out of bin. The old uniform SYSTEM+Admins grant is what made the
    /// app unlaunchable from its shortcut (defect 2), the Settings uninstaller unrunnable (defect 1)
    /// and the helper unstartable (defects 4/7): the Administrators SID is DENY_ONLY in a
    /// UAC-filtered token, so a normal desktop session had no access at all.
    /// </summary>
    private static void HardenDirectoryAcls(string root)
    {
        // "" root and bin -> Users read+execute; Agent -> SYSTEM+Admins only; Session -> Users
        // modify, because Teamscop.App (asInvoker) writes the staff token there and Agent's lock
        // would otherwise make a staff Join silently persist nothing.
        foreach (var sub in new[] { "", "Agent", "bin", LocalAgentStore.StaffSessionDirectory })
        {
            var path = string.IsNullOrEmpty(sub) ? root : Path.Combine(root, sub);
            var users = string.IsNullOrEmpty(sub)
                ? InstallerAcl.UsersAccess.ReadExecute
                : InstallerAcl.AccessFor(sub);
            try
            {
                Directory.CreateDirectory(path);
                // Reset inheritance, then apply the per-directory grant plan.
                RunQuiet(SystemExe("icacls.exe"), $"\"{path}\" /inheritance:r");
                RunQuiet(SystemExe("icacls.exe"), $"\"{path}\" /grant:r {InstallerAcl.GrantArguments(users)}");
            }
            catch
            {
                // Non-fatal — install still usable if ACL tools unavailable.
            }
        }
    }

    private static void RunQuiet(string fileName, string args)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        p?.WaitForExit(30_000);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            // Clear read-only flags that block recursive delete.
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch
                {
                    // ignore per-file; Directory.Delete may still succeed later
                }
            }

            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // ignore — caller retries / reports
        }
    }

    private static bool IsSupportedWindows()
    {
        // .NET 8 supports Windows 10+ (Windows 11 reports as 10.0 build ≥ 22000).
        // We ship win-x64 only.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            return false;
        }

        var arch = RuntimeInformation.OSArchitecture;
        return arch is Architecture.X64 or Architecture.Arm64;
    }

    private static string DescribeOs()
    {
        var v = Environment.OSVersion.Version;
        var arch = RuntimeInformation.OSArchitecture;
        var label = v.Build >= 22000 ? "Windows 11" : "Windows " + v.Major + "." + v.Minor;
        return label + " (build " + v.Build + ", " + arch + ")";
    }

    // The build stamp rides in every dialog title. Several debugging rounds were lost to machines
    // quietly running a stale installer copy — with the stamp on screen, "which build is this?"
    // is answered before the question is asked.
    private static void Ok(string message)
        => MessageBoxW(IntPtr.Zero, message, "Teamscop Setup " + ProductVersion(), 0x00000040); // MB_ICONINFORMATION

    private static void Fail(string message)
        => MessageBoxW(IntPtr.Zero, message, "Teamscop Setup " + ProductVersion(), 0x00000010); // MB_ICONERROR

    private static bool AskYesNo(string message)
    {
        // MB_YESNO | MB_ICONQUESTION
        var r = MessageBoxW(IntPtr.Zero, message, "Teamscop Setup " + ProductVersion(), 0x00000004 | 0x00000020);
        return r == 6; // IDYES
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
