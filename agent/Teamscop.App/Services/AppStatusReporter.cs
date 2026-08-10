using Teamscop.App.Composition;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.Services;

/// <summary>
/// §14.2 — the second, independent reporting channel.
///
/// The monitoring service reports on itself, which works right up until it stops running: a dead
/// service and a powered-off PC produce exactly the same thing, silence. This reporter runs in the
/// desktop app, as the signed-in user, in a different process — so when the service goes quiet the
/// server can still hear someone on that machine say why. That is the whole mechanism behind the
/// "online but broken" state; there is no clever trick underneath it.
///
/// It is deliberately weak by construction. It posts to its own route, which never touches the
/// service's liveness fields, and the classifier consults it only in rules that produce "broken".
/// Nothing it says can make a machine look healthy — which matters, because this process runs as
/// the monitored employee and they are a local administrator on their own PC.
/// </summary>
public sealed class AppStatusReporter : IAsyncDisposable
{
    /// <summary>Three of these may be missed before the server stops believing the channel.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly TeamscopApi _api;
    private readonly SessionStore _session;
    private readonly UiLog _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public AppStatusReporter(TeamscopApi api, SessionStore session, UiLog log)
    {
        _api = api;
        _session = session;
        _log = log;
    }

    /// <summary>Starts reporting. Safe to call twice; the second call does nothing.</summary>
    public void Start()
    {
        if (_loop is not null || !OperatingSystem.IsWindows())
        {
            return;
        }

        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ReportOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never fatal, and never surfaced to the employee. A machine that cannot reach the
                // server simply goes quiet, and quiet is honestly reported as offline.
                _log.Debug($"App status report failed: {ex.Message}");
            }

            try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReportOnceAsync(CancellationToken ct)
    {
        var state = _session.State;
        if (string.IsNullOrWhiteSpace(state.AccessToken)
            || string.Equals(state.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            // An admin's own PC is never monitored, so it has no status to report.
            return;
        }

        // An install stops the service and replaces every binary. Reporting through that window
        // would accuse the whole fleet of sabotage on every routine upgrade.
        if (InstallInProgress())
        {
            return;
        }

        var serviceState = ServiceProbe.Current();

        // The service checks the app's files and the app checks the service's, so deleting either
        // half is noticed by the other. Existence only — see ComponentInventory.
        var missing = ComponentInventory.MissingFrom(
            ComponentInventory.DefaultBinDirectory,
            ComponentInventory.AppSideComponents);

        await _api.AppReportAsync(
            new AppStatusReport(
                ServiceState: ToWireValue(serviceState),
                MissingComponents: missing.Count == 0 ? null : string.Join(",", missing)),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// True while the installer is mid-run. Read from HKLM, which the installer owns and which an
    /// unelevated process cannot forge — an employee who could set this at will could silence the
    /// channel that reports them.
    /// </summary>
    private static bool InstallInProgress()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Teamscop");
            return key?.GetValue("InstallInProgress") is int flag && flag != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// The wire vocabulary the server's classifier understands. Unknown is load-bearing: it means
    /// "no claim", and the server will never darken a machine on it.
    /// </summary>
    private static string ToWireValue(ServiceState state) => state switch
    {
        ServiceState.Running => "running",
        ServiceState.StartPending => "start_pending",
        ServiceState.Stopped => "stopped",
        ServiceState.Disabled => "disabled",
        ServiceState.NotInstalled => "not_installed",
        ServiceState.ExeMissing => "exe_missing",
        _ => "unknown"
    };

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { /* expected */ }
        }

        _cts.Dispose();
    }
}
