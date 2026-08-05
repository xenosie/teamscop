using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.AdminHost;

/// <summary>
/// Admin desktop host. Close / Ctrl+C fully ends this process.
/// WinUI shell can replace this console host later without changing lifecycle policy.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var policy = RolePolicy.For(AgentRole.Admin);
        Console.WriteLine("Teamscop Admin Host");
        Console.WriteLine($"Role policy: AllowClose={policy.AllowUserClose} InstallRoot={policy.RecommendedInstallRoot}");
        Console.WriteLine("Close this window or press Ctrl+C to exit completely.");

        var apiBase = args.FirstOrDefault(a => a.StartsWith("--api=", StringComparison.OrdinalIgnoreCase))
            ?[6..] ?? "https://teamscop.com";
        var store = new LocalAgentStore(AgentRole.Admin);
        var state = store.Load();
        state.ApiBaseUrl ??= apiBase;
        state.Role = "admin";

        if (string.IsNullOrWhiteSpace(state.DeviceKey))
        {
            state.DeviceKey = new DeviceKeyProvider().GetDeviceKey();
            store.Save(state);
        }

        Console.WriteLine($"DeviceKey: {state.DeviceKey}");
        Console.WriteLine($"State file: {store.StatePath}");
        Console.WriteLine();
        Console.WriteLine("Commands: enroll-totp | status | quit");

        using var lifecycle = new LifecycleApiClient(state.ApiBaseUrl ?? apiBase);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        while (!cts.IsCancellationRequested)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line is null || cts.IsCancellationRequested)
            {
                break;
            }

            var cmd = line.Trim().ToLowerInvariant();
            if (cmd is "quit" or "exit" or "close")
            {
                break;
            }

            if (cmd == "enroll-totp")
            {
                if (string.IsNullOrWhiteSpace(state.AccessToken))
                {
                    Console.WriteLine("Login first (set AccessToken in agent-state.json after auth).");
                    continue;
                }

                try
                {
                    var enroll = await lifecycle.EnrollTotpAsync(state.AccessToken, cts.Token);
                    Console.WriteLine("TOTP enrolled. Add this to your authenticator app:");
                    Console.WriteLine(enroll.OtpAuthUri);
                    Console.WriteLine($"Secret: {enroll.Secret}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Enroll failed: {ex.Message}");
                }

                continue;
            }

            if (cmd == "status")
            {
                Console.WriteLine($"Token present: {!string.IsNullOrWhiteSpace(state.AccessToken)}");
                continue;
            }

            Console.WriteLine("Unknown command.");
        }

        Console.WriteLine("Admin host exiting.");
        return 0;
    }
}
