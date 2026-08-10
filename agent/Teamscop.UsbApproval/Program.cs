using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Teamscop.Engine.Usb;
using Teamscop.UsbApproval.Views;

namespace Teamscop.UsbApproval;

/// <summary>
/// §7.3 — the small always-on-top USB approval box, shown on the employee's desktop when mass storage
/// is inserted on a monitored staff PC. It reads the request file, collects a 6-digit code, and
/// writes the response file the StaffService is waiting on. The service verifies the code offline
/// (§9.6) and unlocks the specific device on success.
///
/// Exit codes: 0 code submitted, 1 startup failure, 2 cancelled.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    [STAThread]
    public static int Main(string[] args)
    {
        var reqPath = GetArg(args, "--request");
        var respPath = GetArg(args, "--response");
        if (string.IsNullOrWhiteSpace(reqPath) || string.IsNullOrWhiteSpace(respPath) || !File.Exists(reqPath))
        {
            return 2;
        }

        UsbApprovalRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<UsbApprovalRequest>(File.ReadAllText(reqPath), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return 2;
        }

        if (request is null)
        {
            return 2;
        }

        var exitCode = 2;
        try
        {
            // Window constructed in App.OnFrameworkInitializationCompleted, never in the lifetime
            // callback — the callback runs before the windowing platform exists and a Window built
            // there throws, which had this sticker exiting 1 on every run without ever showing.
            App.Request = request;
            App.ResponsePath = respPath;
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
        return hit?[prefix.Length..].Trim('"');
    }
}
