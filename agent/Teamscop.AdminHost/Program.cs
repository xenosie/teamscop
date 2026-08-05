using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.AdminHost;

/// <summary>
/// Admin desktop host. Close / Ctrl+C fully ends this process.
/// Per-staff TOTP key generator for USB approve + uninstall.
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
        Console.WriteLine("Commands:");
        Console.WriteLine("  staff                     — list staff + TOTP status");
        Console.WriteLine("  enroll-totp <staffId>     — enroll per-staff TOTP (USB + uninstall)");
        Console.WriteLine("  code <staffId>            — generate current 6-digit code");
        Console.WriteLine("  quit");

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

            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            var cmd = parts[0].ToLowerInvariant();
            if (cmd is "quit" or "exit" or "close")
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(state.AccessToken)
                && cmd is "staff" or "enroll-totp" or "code")
            {
                Console.WriteLine("Login first (set AccessToken in agent-state.json after auth).");
                continue;
            }

            try
            {
                if (cmd == "staff")
                {
                    var list = await lifecycle.ListStaffTotpAsync(state.AccessToken!, cts.Token);
                    if (list.Count == 0)
                    {
                        Console.WriteLine("(no staff)");
                        continue;
                    }

                    foreach (var s in list)
                    {
                        Console.WriteLine(
                            $"{s.StaffUserId}  {s.StaffUsername,-20}  totp={(s.Enabled ? "enrolled" : "missing")}");
                    }

                    continue;
                }

                if (cmd == "enroll-totp")
                {
                    if (parts.Length < 2 || !Guid.TryParse(parts[1], out var staffId))
                    {
                        Console.WriteLine("Usage: enroll-totp <staffUserId>");
                        continue;
                    }

                    var enroll = await lifecycle.EnrollTotpAsync(state.AccessToken!, staffId, cts.Token);
                    Console.WriteLine($"TOTP enrolled for {enroll.StaffUsername} (USB + uninstall).");
                    Console.WriteLine("Add to authenticator OR use `code <staffId>` generator:");
                    Console.WriteLine(enroll.OtpAuthUri);
                    Console.WriteLine($"Secret: {enroll.Secret}");
                    continue;
                }

                if (cmd == "code")
                {
                    if (parts.Length < 2 || !Guid.TryParse(parts[1], out var staffId))
                    {
                        Console.WriteLine("Usage: code <staffUserId>");
                        continue;
                    }

                    var code = await lifecycle.GetTotpCodeAsync(state.AccessToken!, staffId, cts.Token);
                    Console.WriteLine(
                        $"{code.StaffUsername}: {code.Code}  (valid ~{code.RemainingSeconds}s) — USB or uninstall");
                    continue;
                }

                if (cmd == "status")
                {
                    Console.WriteLine($"Token present: {!string.IsNullOrWhiteSpace(state.AccessToken)}");
                    continue;
                }

                Console.WriteLine("Unknown command.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed: {ex.Message}");
            }
        }

        Console.WriteLine("Admin host exiting.");
        return 0;
    }
}
