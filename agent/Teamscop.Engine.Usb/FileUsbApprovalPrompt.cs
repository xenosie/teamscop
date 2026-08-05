using System.Diagnostics;
using System.Text.Json;

namespace Teamscop.Engine.Usb;

/// <summary>
/// Launches Teamscop.UsbApproval sticker helper and waits for a response file.
/// Falls back to console sticker when helper exe is missing (dev/CI).
/// </summary>
public sealed class FileUsbApprovalPrompt : IUsbApprovalPrompt
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _workDir;
    private readonly string? _helperExe;

    public FileUsbApprovalPrompt(string workDir, string? helperExe = null)
    {
        _workDir = workDir;
        Directory.CreateDirectory(_workDir);
        _helperExe = helperExe ?? FindHelper();
    }

    public async Task<string?> PromptForTotpAsync(UsbApprovalRequest request, CancellationToken ct = default)
    {
        var reqPath = Path.Combine(_workDir, $"usb-req-{request.RequestId}.json");
        var respPath = Path.Combine(_workDir, $"usb-resp-{request.RequestId}.json");
        try
        {
            await File.WriteAllTextAsync(reqPath, JsonSerializer.Serialize(request, Json), ct).ConfigureAwait(false);
            if (File.Exists(respPath))
            {
                File.Delete(respPath);
            }

            if (!string.IsNullOrWhiteSpace(_helperExe) && File.Exists(_helperExe))
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = _helperExe,
                    Arguments = $"--request=\"{reqPath}\" --response=\"{respPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = false
                });
                if (proc is not null)
                {
                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                }
            }
            else
            {
                // Console sticker fallback (dev / service without helper beside it)
                Console.WriteLine();
                Console.WriteLine("┌──────────────────────────────────────────┐");
                Console.WriteLine("│  Teamscop — USB storage blocked          │");
                Console.WriteLine("│  Enter admin 6-digit code to approve     │");
                Console.WriteLine($"│  Device: {(request.Device.FriendlyName ?? request.Device.InstanceId),-30} │");
                Console.WriteLine("└──────────────────────────────────────────┘");
                Console.Write("TOTP code: ");
                var code = Console.ReadLine()?.Trim();
                var resp = new UsbApprovalResponse(request.RequestId, !string.IsNullOrWhiteSpace(code), code, null);
                await File.WriteAllTextAsync(respPath, JsonSerializer.Serialize(resp, Json), ct).ConfigureAwait(false);
            }

            if (!File.Exists(respPath))
            {
                return null;
            }

            var parsed = JsonSerializer.Deserialize<UsbApprovalResponse>(
                await File.ReadAllTextAsync(respPath, ct).ConfigureAwait(false), Json);
            if (parsed is null || !parsed.Approved || string.IsNullOrWhiteSpace(parsed.TotpCode))
            {
                return null;
            }

            return parsed.TotpCode.Trim();
        }
        finally
        {
            TryDelete(reqPath);
            TryDelete(respPath);
        }
    }

    private static string? FindHelper()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "Teamscop.UsbApproval.exe"),
            Path.Combine(baseDir, "Teamscop.UsbApproval"),
            Path.Combine(baseDir, "..", "Teamscop.UsbApproval", "Teamscop.UsbApproval.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
