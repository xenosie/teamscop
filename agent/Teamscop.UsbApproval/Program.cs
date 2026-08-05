using System.Text.Json;
using Teamscop.Engine.Usb;

namespace Teamscop.UsbApproval;

/// <summary>
/// Small sticker UI shown when USB mass storage is inserted on a staff PC.
/// Console sticker now; WinUI toast/sticker can replace without changing the request/response contract.
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        var reqPath = GetArg(args, "--request");
        var respPath = GetArg(args, "--response");
        if (string.IsNullOrWhiteSpace(reqPath) || string.IsNullOrWhiteSpace(respPath))
        {
            Console.Error.WriteLine("Usage: Teamscop.UsbApproval --request=path --response=path");
            return 2;
        }

        if (!File.Exists(reqPath))
        {
            Console.Error.WriteLine("Request file missing.");
            return 2;
        }

        var request = JsonSerializer.Deserialize<UsbApprovalRequest>(
            await File.ReadAllTextAsync(reqPath), Json);
        if (request is null)
        {
            Console.Error.WriteLine("Invalid request.");
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine("╔════════════════════════════════════════════╗");
        Console.WriteLine("║  Teamscop                                  ║");
        Console.WriteLine("║  USB storage blocked                       ║");
        Console.WriteLine("║  Ask admin for a 6-digit code to approve   ║");
        Console.WriteLine("╠════════════════════════════════════════════╣");
        var name = request.Device.FriendlyName ?? request.Device.InstanceId;
        if (name.Length > 40) name = name[..40];
        Console.WriteLine($"║  Device: {name,-34}║");
        if (!string.IsNullOrWhiteSpace(request.Device.DriveLetter))
        {
            Console.WriteLine($"║  Drive:  {request.Device.DriveLetter,-34}║");
        }

        Console.WriteLine("╚════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.Write("Password / TOTP code: ");
        var code = Console.ReadLine()?.Trim() ?? string.Empty;

        UsbApprovalResponse response;
        if (code.Length != 6 || !code.All(char.IsDigit))
        {
            response = new UsbApprovalResponse(request.RequestId, false, null, "Invalid code format.");
            await File.WriteAllTextAsync(respPath, JsonSerializer.Serialize(response, Json));
            Console.Error.WriteLine("Invalid code format.");
            return 1;
        }

        response = new UsbApprovalResponse(request.RequestId, true, code, null);
        await File.WriteAllTextAsync(respPath, JsonSerializer.Serialize(response, Json));
        Console.WriteLine("Code submitted. Waiting for agent to unlock this USB session…");
        return 0;
    }

    private static string? GetArg(string[] args, string name)
    {
        var prefix = name + "=";
        var hit = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return hit?[prefix.Length..].Trim('"');
    }
}
