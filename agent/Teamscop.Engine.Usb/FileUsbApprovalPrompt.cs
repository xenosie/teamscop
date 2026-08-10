using System.Diagnostics;
using System.Text.Json;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.Engine.Usb;

/// <summary>
/// Launches the Teamscop.UsbApproval sticker and waits for its response file.
///
/// The sticker must appear on the employee's desktop, and the process asking for it is a
/// LocalSystem service in session 0 — so the launch goes through
/// <see cref="InteractiveProcessLauncher"/> first and only falls back to a plain
/// <see cref="Process.Start"/> when that is unavailable (developer run, no console session).
/// </summary>
public sealed class FileUsbApprovalPrompt : IUsbApprovalPrompt
{
    /// <summary>A sticker nobody answers must not hold the approval queue forever.</summary>
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(5);

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

            if (string.IsNullOrWhiteSpace(_helperExe) || !File.Exists(_helperExe))
            {
                return null;
            }

            var arguments = $"--request=\"{reqPath}\" --response=\"{respPath}\"";
            if (OperatingSystem.IsWindows())
            {
                // No plain Process.Start fallback here on purpose. From a LocalSystem service that
                // lands in session 0, where the sticker is invisible and would sit unanswered until
                // the timeout, holding the approval queue. If there is no console session — at boot,
                // before anyone has logged on — the stick simply stays blocked until it is replugged.
                TryShowOnUserDesktop(_helperExe, arguments);
            }
            else
            {
                await RunInProcessSessionAsync(_helperExe, arguments, ct).ConfigureAwait(false);
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

    private static bool TryShowOnUserDesktop(string exePath, string arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return InteractiveProcessLauncher.TryRunInActiveSession(exePath, arguments, PromptTimeout);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static async Task RunInProcessSessionAsync(string exePath, string arguments, CancellationToken ct)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = false
        });
        if (proc is null)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PromptTimeout);
        try
        {
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
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
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind; the next request uses a fresh id.
        }
    }
}
