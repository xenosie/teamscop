using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Tracking;
using Teamscop.UninstallGuard.Views;

namespace Teamscop.UninstallGuard;

/// <summary>
/// The uninstall sticker. It asks for the admin's 6-digit code and checks it against the secret
/// this machine holds — no network, because §11.2 says uninstall must work offline and the old
/// path made two HTTP calls that both failed closed with no connectivity.
///
/// Exit codes: 0 authorized, 1 startup failure, 2 cancelled or refused.
///
/// Accepting a code does NOT touch the machine. Undoing the USB gate and deleting the approval
/// secret happen only under <c>--restore-machine</c>, which the uninstaller invokes once removal
/// is genuinely under way. Doing it here on code entry meant a single legitimately issued code
/// permanently unblocked USB and destroyed the offline credential even when the user then
/// cancelled the uninstall and the agent kept running — defeating §9.4.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var exitCode = 2;
        try
        {
            var store = new LocalAgentStore(AgentRole.Staff);
            var state = store.Load();
            var agentRoot = UninstallAudit.ResolveAgentRoot(store);

            // Session file first, HKLM mirror second — the same order and the same values the
            // uninstaller's own gate uses. Without the fallback, deleting the user-writable session
            // file left setup demanding a code this window could never verify: an unsatisfiable gate.
            var deviceKey = state.DeviceKey;
            if (string.IsNullOrWhiteSpace(deviceKey) && OperatingSystem.IsWindows())
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Teamscop");
                    deviceKey = (key?.GetValue("DeviceKey") as string)?.Trim();
                }
                catch
                {
                    // Fall through with whatever the session file gave us.
                }
            }

            // Invoked by the uninstaller after it has committed to removing the product. No UI,
            // no code prompt — authorization already happened, this is the cleanup half.
            if (args.Any(a => string.Equals(a, "--restore-machine", StringComparison.OrdinalIgnoreCase)))
            {
                UninstallAudit.RestoreMachineAndForgetSecrets(agentRoot);
                return 0;
            }

            // Invoked by the uninstaller at the point of commitment — authorization has passed and
            // teardown is about to start.
            //
            // The record used to fire from the sticker's on-accept callback, which is minutes too
            // early: the service keeps heartbeating throughout the rest of the flow, and a user who
            // entered a valid code and then cancelled left a permanent "uninstalled" record behind
            // on a machine that is still fully installed. Recording here means the event exists if
            // and only if the product is actually being removed.
            if (args.Any(a => string.Equals(a, "--record-uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                UninstallAudit
                    .RecordAsync(agentRoot, GetArg(args, "--api") ?? state.ApiBaseUrl, state.AccessToken, deviceKey)
                    .GetAwaiter()
                    .GetResult();
                return 0;
            }

            var apiBase = GetArg(args, "--api") ?? state.ApiBaseUrl;

            // §6.3/§6.4 — verify offline by deriving this machine's code from its device key on the
            // last-known company-local time. The running service persists the zone to
            // business-clock.json; the same BusinessClock resolution is reused here so the guard and
            // the server compute the identical time step (missing file → UTC, the accepted §6.6 edge).
            var clock = new BusinessClock();
            clock.Apply(BusinessClockStore.Load(agentRoot));
            var verifier = new LocalApprovalVerifier(
                deviceKey: () => deviceKey,
                stateDirectory: agentRoot,
                companyNow: () => new DateTimeOffset(clock.Now().BusinessLocal, TimeSpan.Zero));

            // The window is constructed inside App.OnFrameworkInitializationCompleted, never here:
            // the lifetime callback runs before the windowing platform exists, and a Window created
            // that early throws — which made this process exit 1 on every run and the uninstaller
            // read "refused". The verifier is handed over statically for the same reason.
            App.Verifier = verifier;
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            exitCode = App.ResultExitCode;
        }
        catch
        {
            return 1;
        }

        return exitCode;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static string? GetArg(string[] args, string name)
    {
        var prefix = name + "=";
        var hit = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return hit?[prefix.Length..];
    }
}
