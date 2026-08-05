using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.UninstallGuard;

/// <summary>
/// Called by the Windows installer as a custom action before staff uninstall proceeds.
/// Shows a password/TOTP prompt (console modal here; WinUI/WinForms modal on Windows packaging).
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("========================================");
        Console.WriteLine(" Teamscop Uninstall Authorization");
        Console.WriteLine(" Enter the 6-digit admin TOTP code");
        Console.WriteLine("========================================");

        var apiBase = GetArg(args, "--api") ?? "https://teamscop.com";
        var deviceKey = GetArg(args, "--deviceKey");
        if (string.IsNullOrWhiteSpace(deviceKey))
        {
            var store = new LocalAgentStore(AgentRole.Staff);
            deviceKey = store.Load().DeviceKey ?? new DeviceKeyProvider().GetDeviceKey();
        }

        Console.Write("TOTP code: ");
        var code = Console.ReadLine()?.Trim() ?? string.Empty;
        if (code.Length != 6 || !code.All(char.IsDigit))
        {
            Console.Error.WriteLine("Invalid code format.");
            return 2;
        }

        try
        {
            using var client = new LifecycleApiClient(apiBase);
            var ticket = await client.VerifyUninstallAsync(deviceKey, code);
            var ticketPath = Path.Combine(Path.GetTempPath(), "teamscop-uninstall.ticket");
            await File.WriteAllTextAsync(ticketPath, ticket.UninstallTicket);
            Console.WriteLine("Authorization granted. Uninstall may continue.");
            Console.WriteLine($"Ticket expires in {ticket.ExpiresIn}s");
            // Exit 0 = MSI custom action allows uninstall to proceed.
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Authorization failed: {ex.Message}");
            return 1;
        }
    }

    private static string? GetArg(string[] args, string name)
    {
        var prefix = name + "=";
        var hit = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return hit?[prefix.Length..];
    }
}
