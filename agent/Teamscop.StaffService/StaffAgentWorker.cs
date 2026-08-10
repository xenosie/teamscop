using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Sync;
using Teamscop.Engine.Tracking;
using Teamscop.Engine.Usb;

namespace Teamscop.StaffService;

/// <summary>
/// Staff background loop: USB gate + tracking + business clock + connectivity + vault + sync flush.
/// </summary>
public sealed class StaffAgentWorker(
    ILogger<StaffAgentWorker> logger,
    IConfiguration configuration,
    IHostEnvironment hostEnvironment,
    IHostApplicationLifetime lifetime,
    LocalAgentStore store,
    LifecycleApiClient lifecycleApi,
    SyncEngine syncEngine,
    IOutboxQueue outbox,
    TrackingCoordinator tracking,
    ConfigRealtimeClient configClient,
    UsbSessionController usb,
    CapturePipeServer capturePipe,
    SessionHelperSupervisor helperSupervisor,
    AgentHealthWriter health,
    HttpClient configHttp) : BackgroundService
{
    private bool _hubStarted;
    private bool _usbStarted;
    private bool _selfRemovalInitiated;
    private int _loopSeconds = 30;
    private DateTimeOffset _lastConfigPull = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastFlushAt;
    private DateTimeOffset? _lastUploadOkAt;

    /// <summary>
    /// Defect 8 — this machine may capture only when it is an enrolled STAFF machine: a staff access
    /// token is present and nothing marks it as the admin's own PC. On an admin machine the staff
    /// service's ProgramData store is never given a token (the admin app writes a separate store),
    /// so this is false there and no capture, no USB gate and no helper are ever started. §4.5 is a
    /// storage rule, not just a display rule.
    /// </summary>
    public static bool CaptureEnabledFor(LocalAgentState state)
        => HasStaffEnrollment(state) && !InstallRoleMarker.IsAdminMachine();

    /// <summary>
    /// The account-level half of the capture gate: a staff access token is present and the role is
    /// not admin. Split out so it can be asserted without a registry (the machine-marker half is
    /// Windows-only). On an admin machine the staff service's ProgramData store is never handed a
    /// token, so this is false and no capture ever starts (defect 8).
    /// </summary>
    public static bool HasStaffEnrollment(LocalAgentState state)
        => !string.IsNullOrWhiteSpace(state.AccessToken)
           && !string.Equals(state.Role, "admin", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// §8.1/§8.2 — an admin PC must never be tracked, and the mechanism is that the service the
    /// installer put on EVERY machine removes itself once the machine's stored role is admin. The
    /// decision is a pure function of the marker so it is unit-testable; the SCM surgery it triggers
    /// is Windows-only and delegated to a detached elevated process (a running service cannot
    /// cleanly <c>sc delete</c> itself). "unassigned" and "staff" do not trigger it — only "admin".
    /// </summary>
    public static bool ShouldSelfRemove(string? installedRole)
        => string.Equals(installedRole?.Trim(), InstallRoleMarker.Admin, StringComparison.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var companyKey = configuration["Agent:CompanyTokenKey"] ?? CompanyTokenKey.Base64;
        if (CompanyTokenKey.IsAllZeroBase64(companyKey) && !hostEnvironment.IsDevelopment())
        {
            // B2 — the all-zero build constant is the development key. Running on it in production
            // means the vault master key and every offline company token are derived from a value
            // published in the source tree, so refuse rather than collect data under a key anyone
            // can reproduce. Stopping cleanly (exit code 0) keeps SCM's recovery actions quiet:
            // a restart loop would bury the one log line that explains the problem.
            logger.LogCritical(
                "Agent:CompanyTokenKey is missing or all-zero under {ContentRoot}. Refusing to start. " +
                "Rebuild the installer with deploy/windows/build-setup.sh so appsettings.Production.json " +
                "carries the company key, then reinstall.",
                AppContext.BaseDirectory);

            // Say it somewhere a person will actually look. This clean exit leaves SCM showing an
            // innocuous "stopped", the Event Log entry is buried, and the machine reads as silently
            // dead — a diagnosis that has cost hours. The health file is what the sticker and the
            // technician read first, so the reason belongs there too.
            WriteHealth(
                helperAlive: false,
                trackingOk: false,
                helperState: "refused_to_start",
                captureUnavailable: "company_key_missing: appsettings.Production.json absent or carries no key",
                pending: 0);
            lifetime.StopApplication();
            return;
        }

        // The last line of defence. A faulted BackgroundService stops the host with EXIT CODE 0,
        // and SCM's recovery actions only fire on failures — so any exception escaping this method
        // used to kill the service permanently while looking like a polite shutdown. If anything
        // gets past the per-iteration handling, die loudly with a non-zero code so SCM restarts us.
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogCritical(ex, "Staff agent faulted; exiting non-zero so SCM restarts the service.");
            WriteHealth(
                helperAlive: false,
                trackingOk: false,
                helperState: "faulted",
                captureUnavailable: $"{ex.GetType().Name}: {ex.Message}",
                pending: SafePendingCount());
            Environment.Exit(1);
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        // §15.2 — cheap hardware. Setting ThreadPriority on this thread is a no-op: it is a
        // thread-pool thread and is returned at the first await.
        TrySetLowPriority();

        var policy = RolePolicy.For(AgentRole.Staff);
        logger.LogInformation(
            "Teamscop staff agent starting. InstallRoot={Root} Service={Service} ContentRoot={ContentRoot}",
            policy.RecommendedInstallRoot,
            ServiceInstallerHints.ServiceName,
            AppContext.BaseDirectory);

        capturePipe.Start();
        logger.LogInformation("SessionHelper capture pipe listening ({Pipe})", capturePipe.PipeName);

        // Wired before anything can push, and before any pull: PullSnapshotAsync raises
        // ConfigChanged itself, so this is the single place a new config is applied.
        WireConfigHandlers(stoppingToken);

        // Defect 8 — the USB gate used to be armed here unconditionally, so the admin's own PC had
        // its ports locked with no possible unlock (§9.4 is about monitored staff, not the console
        // the owner administers from). It now arms inside the loop, only once this machine is an
        // enrolled staff machine.

        _loopSeconds = Math.Max(5, configuration.GetValue("Agent:SyncSeconds", 30));
        var configPullPeriod = TimeSpan.FromSeconds(
            Math.Max(30, configuration.GetValue("Agent:ConfigPullSeconds", 180)));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // §8.2 — the moment this machine registers as admin, the service removes itself. Done
                // FIRST, before any capture/USB/heartbeat work, and once (the detached remover is
                // idempotent, but re-spawning it every loop is noise). Capture is already gated off on
                // an admin machine (CaptureEnabledFor); this is what actually takes the service away.
                if (!_selfRemovalInitiated && ShouldSelfRemove(InstallRoleMarker.Read()))
                {
                    _selfRemovalInitiated = true;
                    logger.LogWarning(
                        "Machine role is 'admin' — an admin PC is never tracked (§8.1). Removing the staff service.");
                    BeginSelfRemoval();
                    lifetime.StopApplication();
                    return;
                }

                var state = store.Load();
                var captureEnabled = CaptureEnabledFor(state);

                // Keep the uninstall gate's copy of the device key current in HKLM, where the
                // monitored user cannot reach it.
                InstallRoleMarker.MirrorDeviceKey(state.DeviceKey);

                // §7 — one "install" row per completed install/upgrade, carrying the installer's own
                // timestamp, so the app history answers "when exactly did this land" precisely.
                if (captureEnabled)
                {
                    await ReportInstallOnceAsync(stoppingToken);
                }

                if (!string.IsNullOrWhiteSpace(state.AccessToken))
                {
                    // §6.2 — per-staff config is a core feature, so the pull runs on its own clock.
                    // It is deliberately not conditioned on the hub: SignalR is a latency
                    // optimisation, not the delivery mechanism.
                    if (DateTimeOffset.UtcNow - _lastConfigPull >= configPullPeriod)
                    {
                        _lastConfigPull = DateTimeOffset.UtcNow;
                        await PullHttpSnapshotsAsync(state.AccessToken, stoppingToken);
                    }

                    if (!_hubStarted)
                    {
                        try
                        {
                            await configClient.StartAsync(state.AccessToken, stoppingToken);
                            _hubStarted = true;
                            logger.LogInformation("Config realtime hub connected");
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Config realtime hub not ready; will retry");
                        }
                    }
                }

                // Arm the USB gate once, and only on an enrolled staff machine (defect 8).
                if (captureEnabled && !_usbStarted)
                {
                    await StartUsbGateAsync(stoppingToken);
                }

                // Keep the interactive capture helper running — the service owns its lifetime, not a
                // registry key. Only on a staff machine: an admin PC captures nothing (defect 8).
                if (captureEnabled)
                {
                    try
                    {
                        helperSupervisor.Ensure();
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Could not ensure the capture helper is running.");
                    }
                }

                // Vault integrity + crash recovery. Never captures (that is the helper's job).
                //
                // Isolated on purpose. This used to sit bare in the loop's single try, AHEAD of the
                // heartbeat — so one throw here silently took the heartbeat, the connectivity record
                // and ingest down with it for the life of the service. The machine then looked
                // simply absent from the admin's side, with nothing on the wire to say why. The
                // heartbeat is the one signal that proves a machine is alive; it must be the most
                // isolated thing in this loop, not the most fragile.
                try
                {
                    await tracking.TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Tracking tick failed; heartbeat and sync continue.");
                }

                var helperAlive = tracking.IsSessionHelperAlive;
                var trackingOk = helperAlive && tracking.IsTrackingHealthy;
                var helperState = tracking.HelperState;
                var captureUnavailable = tracking.CaptureUnavailableReason;
                var pending = syncEngine.PendingCount;

                // Everything that ENQUEUES telemetry — the per-loop connectivity record, the
                // heartbeat — runs only on an enrolled staff machine. On an admin (or not-yet-joined)
                // machine none of it runs, so the outbox does not grow without bound behind a token
                // that will never arrive (defect 8, §13.1).
                if (captureEnabled)
                {
                    // captureEnabled implies a non-empty staff token (HasStaffEnrollment).
                    var accessToken = state.AccessToken!;

                    // The probe writes the connectivity record. It must never decide whether this
                    // machine reports: it is a separate GET /health on a separate HttpClient, so it
                    // can fail for reasons that have nothing to do with the API being reachable —
                    // and when it did, the heartbeat and the outbox flush below were both skipped on
                    // every iteration. The machine then went completely dark to the admin while
                    // still happily pulling its config every 30s. Probe failure is logged and
                    // recorded; it no longer gates anything.
                    ConnectivityStatus? status = null;
                    try
                    {
                        status = await syncEngine.ProbeAndRecordAsync(stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "Connectivity probe failed; reporting continues.");
                    }

                    var bizNow = tracking.BusinessClock.Now();
                    pending = syncEngine.PendingCount;
                    logger.LogDebug(
                        "online={Online} pending={Pending} biz={Biz} v={ClockV}",
                        status?.ApiReachable,
                        pending,
                        bizNow.BusinessLocalIso,
                        bizNow.ClockVersion);

                    await syncEngine.EnqueueAsync(
                        OutboxItem.Create(AgentEventTypes.Heartbeat, new
                        {
                            deviceKey = state.DeviceKey,
                            role = state.Role ?? "staff",
                            pending,
                            pendingOutbox = pending,
                            helperAlive,
                            trackingOk,
                            // "never_started" vs "stale" vs "alive" — so the admin health card can
                            // say WHICH, instead of a total blackout reading as a healthy install.
                            helperState,
                            captureUnavailable,
                            businessLocal = bizNow.BusinessLocalIso,
                            businessTimeZoneId = bizNow.TimeZoneId,
                            businessClockVersion = bizNow.ClockVersion,
                            businessSynchronized = bizNow.Synchronized
                        }),
                        stoppingToken);

                    // Always attempt. Both calls already handle their own failure, and an attempt that
                    // fails costs one timed-out request — far cheaper than a machine that looks
                    // uninstalled because a health check disagreed with reality. The heartbeat is the
                    // only evidence the admin has that a PC is alive, so it gets tried every loop.
                    // §14 — the machine's own verdict rides on the heartbeat. Only the machine can
                    // tell an empty office from a broken pipeline, so it decides and the server
                    // believes it; the verdict can only ever make this machine look worse.
                    var health = BuildHealthReport(tracking, helperAlive, trackingOk, captureUnavailable);

                    try { await lifecycleApi.HeartbeatAsync(accessToken, health, stoppingToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "Lifecycle heartbeat failed");
                    }

                    try
                    {
                        _lastFlushAt = DateTimeOffset.UtcNow;
                        var flushed = await syncEngine.FlushAsync(accessToken, stoppingToken);
                        _lastUploadOkAt = DateTimeOffset.UtcNow;
                        if (flushed > 0)
                        {
                            logger.LogInformation("Flushed {Count} outbox events", flushed);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                    {
                        // Offline or rejected: the outbox keeps the events for the next loop.
                        logger.LogWarning(ex, "Outbox flush failed; {Pending} events retained.", pending);
                    }

                    pending = syncEngine.PendingCount;
                }

                WriteHealth(helperAlive, trackingOk, helperState, captureUnavailable, pending);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Staff agent iteration failed; will retry.");

                // WriteHealth sits at the end of the try, so a throw anywhere above it left
                // agent-health.json stale — or, on a machine that never completed one clean
                // iteration, absent entirely. The only remaining evidence was a Windows Event Log
                // entry, which is exactly the thing nobody can read remotely. Record the reason
                // here so a failing agent always says why, in a file, on every loop.
                WriteHealth(
                    helperAlive: false,
                    trackingOk: false,
                    helperState: "iteration_failed",
                    captureUnavailable: $"{ex.GetType().Name}: {ex.Message}",
                    pending: SafePendingCount());
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_loopSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Orderly stop only — enqueue power_off and best-effort flush before tearing down IO.
        try
        {
            var state = store.Load();
            var biz = tracking.BusinessClock.Now();
            await PowerOffEmitter.EmitAsync(
                outbox,
                syncEngine,
                state.AccessToken,
                PowerOffEmitter.ReasonServiceStop,
                businessLocal: biz.BusinessLocalIso,
                businessTimeZoneId: biz.TimeZoneId,
                businessClockVersion: biz.ClockVersion,
                businessSynchronized: biz.Synchronized,
                cancellationToken: CancellationToken.None);
            logger.LogInformation("Enqueued power_off (service_stop) tz={Tz}", biz.TimeZoneId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to enqueue power_off on stop");
        }

        await configClient.DisposeAsync();
        await usb.DisposeAsync();
        await capturePipe.DisposeAsync();
        logger.LogInformation("Teamscop staff agent stopping.");
    }

    private void TrySetLowPriority()
    {
        try
        {
            System.Diagnostics.Process.GetCurrentProcess().PriorityClass =
                System.Diagnostics.ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug(ex, "Could not lower process priority");
        }
    }

    /// <summary>When a console user was first seen in the current stretch. Resets when they leave.</summary>
    private DateTimeOffset? _consoleUserSince;

    /// <summary>
    /// The verdict this machine sends the server: is capture working, and are the product's files
    /// still on disk.
    ///
    /// The file check covers the case the health signal alone cannot: an employee who deletes
    /// binaries out of the install directory leaves the running processes holding their code in
    /// memory, so the service keeps reporting itself perfectly healthy while the product is gutted.
    /// The service checks the components it does NOT run — the app, the helper, the guard — and the
    /// desktop app returns the favour, so whichever half survives reports the damage.
    /// </summary>
    private AgentHealthReport BuildHealthReport(
        TrackingCoordinator tracking,
        bool helperAlive,
        bool trackingOk,
        string? captureUnavailable)
    {
        var sessions = InteractiveSessions.Current();
        _consoleUserSince = sessions.ActiveConsoleUser
            ? _consoleUserSince ?? DateTimeOffset.UtcNow
            : null;

        var config = tracking.Config;
        var verdict = CaptureState.Evaluate(
            anyCaptureEnabled: config.ScreenshotEnabled || config.TimeTrackEnabled || config.BrowserHistoryEnabled,
            sessions: sessions,
            consoleUserSince: _consoleUserSince,
            helperAlive: helperAlive,
            trackingHealthy: trackingOk,
            unavailableReason: captureUnavailable,
            now: DateTimeOffset.UtcNow);

        var missing = ComponentInventory.MissingFrom(
            ComponentInventory.DefaultBinDirectory,
            ComponentInventory.ServiceSideComponents);

        return new AgentHealthReport(
            verdict.State,
            verdict.Reason,
            missing.Count == 0 ? null : string.Join(",", missing));
    }

    /// <summary>Last-write time of the running executable — see AgentHealthSnapshot.ServiceBuildUtc.</summary>
    private static readonly DateTimeOffset? ServiceBuildStamp = ReadBuildStamp();

    private static DateTimeOffset? ReadBuildStamp()
    {
        try
        {
            var exe = Environment.ProcessPath;
            return string.IsNullOrEmpty(exe) ? null : new DateTimeOffset(File.GetLastWriteTimeUtc(exe), TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }

    private bool _installReported;

    /// <summary>
    /// Emits one <c>install</c> event when the installer's HKLM stamp is newer than the last one
    /// this machine reported. The event's OccurredAt is the INSTALLER's timestamp — the moment the
    /// product actually landed — so the admin's app history shows the exact install time even
    /// though the report goes out minutes later, once the service is up and enrolled.
    /// </summary>
    private async Task ReportInstallOnceAsync(CancellationToken ct)
    {
        if (_installReported || !OperatingSystem.IsWindows())
        {
            return;
        }

        _installReported = true; // one attempt per process lifetime; the marker file is the real gate
        try
        {
            string? installedAtRaw = null;
            string? stamp = null;
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Teamscop"))
            {
                installedAtRaw = key?.GetValue("InstalledAtUtc") as string;
                stamp = key?.GetValue("InstallStamp") as string;
            }

            if (string.IsNullOrWhiteSpace(installedAtRaw))
            {
                return; // installed by a build older than the stamping installer
            }

            var markerPath = Path.Combine(
                Path.GetDirectoryName(store.StatePath) ?? ".", "install-reported.txt");
            if (File.Exists(markerPath)
                && string.Equals(File.ReadAllText(markerPath).Trim(), installedAtRaw, StringComparison.Ordinal))
            {
                return; // this install has already been reported
            }

            var occurredAt = DateTimeOffset.TryParse(installedAtRaw, out var at) ? at : DateTimeOffset.UtcNow;
            await syncEngine.EnqueueAsync(
                OutboxItem.Create(
                    AgentEventTypes.Install,
                    new { kind = AgentEventTypes.Install, stamp, installedAtUtc = installedAtRaw },
                    occurredAt),
                ct);
            File.WriteAllText(markerPath, installedAtRaw);
            logger.LogInformation("Reported install {Stamp} at {At}", stamp, installedAtRaw);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Install report skipped");
            _installReported = false; // transient — try again next loop
        }
    }

    /// <summary>Outbox depth, or -1 when even that cannot be read — used on the failure path.</summary>
    private int SafePendingCount()
    {
        try { return syncEngine.PendingCount; }
        catch { return -1; }
    }

    private void WriteHealth(bool helperAlive, bool trackingOk, string helperState, string? captureUnavailable, int pending)
    {
        try
        {
            health.Write(new AgentHealthSnapshot
            {
                WrittenAtUtc = DateTimeOffset.UtcNow,
                HelperAlive = helperAlive,
                TrackingOk = trackingOk,
                HelperState = helperState,
                CaptureUnavailableReason = captureUnavailable,
                ServiceBuildUtc = ServiceBuildStamp,
                PendingOutbox = pending,
                LoopSeconds = _loopSeconds,
                LastCaptureAtUtc = capturePipe.LastCaptureAcceptedAtUtc,
                LastFlushAtUtc = _lastFlushAt,
                LastUploadOkAtUtc = _lastUploadOkAt
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not write {File}", AgentHealthWriter.FileName);
        }
    }

    /// <summary>
    /// Arms the machine-wide USB mass-storage gate. Called once, and only on an enrolled staff
    /// machine — arming it on the admin's own PC would lock the owner out of their ports with no
    /// unlock (defect 8). Failure is non-fatal; the approval path still works.
    /// </summary>
    private async Task StartUsbGateAsync(CancellationToken ct)
    {
        try
        {
            await usb.StartAsync(ct);
            _usbStarted = true;
            logger.LogInformation(
                "USB mass-storage gate active. FloorApplied={Floor} FloorSupported={FloorSupported} DeviceGate={Gate}",
                usb.FloorApplied,
                usb.FloorSupported,
                usb.GateSupported);
            if (!usb.GateSupported)
            {
                logger.LogWarning(
                    "USB device gate unavailable — inserted storage will be visible but unreadable "
                    + "rather than absent (§9.2 degraded).");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "USB gate failed to start; continuing without USB block");
        }
    }

    private async Task PullHttpSnapshotsAsync(string accessToken, CancellationToken ct)
    {
        // Each pull gets its own try: one failing endpoint must not skip the rest.
        await PullOneAsync("tracking config", async () =>
        {
            var snap = await configClient.PullSnapshotAsync(configHttp, accessToken, ct);
            if (snap is not null)
            {
                logger.LogInformation("Tracking config pulled v{Version}", snap.ConfigVersion);
            }
        });

        await PullOneAsync("business time", async () =>
        {
            var biz = await configClient.PullBusinessTimeAsync(configHttp, accessToken, ct);
            if (biz is not null)
            {
                tracking.ApplyBusinessClock(biz);
            }
        });

        await PullOneAsync("org placement", async () =>
        {
            var placement = await configClient.PullOrgPlacementAsync(configHttp, accessToken, ct);
            if (placement is not null)
            {
                logger.LogInformation(
                    "Org placement: {Placement} team={Team} v{Version} leader={IsLeader}",
                    placement.Placement, placement.TeamName ?? "-", placement.StructureVersion, placement.IsTeamLeader);
            }
        });

        await PullOneAsync("authorities", async () =>
        {
            var authorities = await configClient.PullAuthoritiesAsync(configHttp, accessToken, ct);
            if (authorities is not null)
            {
                logger.LogInformation(
                    "Authorities v{Version} policeman={IsPolice} packages=[{Packages}]",
                    authorities.AuthorityVersion,
                    authorities.IsPoliceman,
                    string.Join(',', authorities.Packages));
            }
        });

        // §6 — there is no "approval secrets" pull any more: the machine derives its own USB and
        // uninstall codes from its device key on demand (TotpGenerator.DeriveMachineSecret), so
        // nothing has to be provisioned or refreshed for offline verification to work.
    }

    private async Task PullOneAsync(string what, Func<Task> pull)
    {
        try
        {
            await pull();
        }
        catch (Exception ex)
        {
            // Includes cancellations: an HttpClient timeout here is a TaskCanceledException from
            // the request's own token, not a service stop, and one failed pull must never take the
            // loop down. A genuine stop is observed by the loop's own token immediately after.
            logger.LogWarning(ex, "Config pull failed: {What}", what);
        }
    }

    /// <summary>
    /// §8.2 — spawn the detached, already-elevated remover (setup <c>--demote-admin</c>) that does the
    /// SCM surgery this service cannot do to itself, then let the caller StopApplication. Never
    /// throws: a failure here must not wedge the shutdown, and the next boot re-detects the marker.
    /// </summary>
    private void BeginSelfRemoval()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var setup = ResolveSetupExe();
            if (setup is null || !File.Exists(setup))
            {
                logger.LogError(
                    "Cannot self-remove: Teamscop_setup.exe not found (HKLM Teamscop\\InstallRoot missing?).");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = setup,
                Arguments = "--demote-admin",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            logger.LogInformation("Spawned self-removal: {Setup} --demote-admin", setup);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            logger.LogError(ex, "Failed to spawn the self-removal process");
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ResolveSetupExe()
    {
        string? root = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Teamscop");
            root = key?.GetValue("InstallRoot") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Fall back to the default install root below.
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Teamscop");
        }

        return Path.Combine(root, "Teamscop_setup.exe");
    }

    private void WireConfigHandlers(CancellationToken stoppingToken)
    {
        configClient.ConfigChanged += cfg =>
        {
            tracking.ApplyConfig(cfg);
            logger.LogInformation(
                "Tracking config applied v{Version} screenshots={Shots}@{Period}s timetrack={Time} browsing={Browse}",
                cfg.ConfigVersion,
                cfg.ScreenshotEnabled,
                cfg.ScreenshotPeriodSeconds,
                cfg.TimeTrackEnabled,
                cfg.BrowserHistoryEnabled);
        };
        configClient.BusinessTimeChanged += cfg =>
        {
            tracking.ApplyBusinessClock(cfg);
            logger.LogInformation("Business clock synced v{Version} tz={Tz}", cfg.ClockVersion, cfg.TimeZoneId);
        };
        configClient.OrgStructureChanged += org =>
        {
            logger.LogInformation(
                "Org structure updated immediately v{Version} teams={Teams} unassigned={Unassigned}",
                org.StructureVersion, org.Teams.Count, org.UnassignedStaff.Count);
        };
        configClient.AuthoritiesChanged += auth =>
        {
            logger.LogInformation(
                "Authorities updated immediately v{Version} policeman={IsPolice} packages=[{Packages}]",
                auth.AuthorityVersion,
                auth.IsPoliceman,
                string.Join(',', auth.Packages));
        };
        configClient.ReconnectedAsync += async () =>
        {
            try
            {
                var token = store.Load().AccessToken;
                if (string.IsNullOrWhiteSpace(token))
                {
                    return;
                }

                _lastConfigPull = DateTimeOffset.UtcNow;
                await PullHttpSnapshotsAsync(token, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Config re-pull after reconnect failed");
            }
        };
    }
}
