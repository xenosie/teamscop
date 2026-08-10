using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Teamscop.StaffService;

/// <summary>
/// A15 / §14.4 — the engine's own account of itself, so the staff sticker proves tracking is
/// working instead of drawing a bar it cannot vouch for.
///
/// Written next to <c>agent-state.json</c> because that is where the desktop app looks. That
/// directory is locked to SYSTEM and Administrators, and the app runs unelevated, so this one file
/// is explicitly granted read to <c>BUILTIN\Users</c> after every write. It holds counters and
/// timestamps only — nothing an employee learns anything from.
/// </summary>
public sealed class AgentHealthWriter(string directory)
{
    public const string FileName = "agent-health.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path = Path.Combine(EnsureDirectory(directory), FileName);

    private static string EnsureDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        return directory;
    }

    public string FilePath => _path;

    /// <summary>Best effort by design: a sticker that cannot be updated is not a reason to stop tracking.</summary>
    public void Write(AgentHealthSnapshot snapshot)
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
        GrantInteractiveRead(_path);
    }

    private static void GrantInteractiveRead(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            AddUsersRead(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // The sticker falls back to Unknown, which is the honest outcome.
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AddUsersRead(string path)
    {
        var file = new FileInfo(path);
        var security = file.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Read,
            AccessControlType.Allow));
        file.SetAccessControl(security);
    }
}

/// <summary>
/// The contract the desktop app reads (<c>Teamscop.App/Services/AgentHealthReader.cs</c>).
/// Additive fields are safe — the reader ignores members it does not know.
/// </summary>
public sealed record AgentHealthSnapshot
{
    public required DateTimeOffset WrittenAtUtc { get; init; }
    public required bool HelperAlive { get; init; }
    public required bool TrackingOk { get; init; }

    /// <summary>never_started / stale / alive — the sticker distinguishes "no helper yet" from "gone quiet".</summary>
    public string? HelperState { get; init; }

    /// <summary>Why capture is unavailable (null when healthy), so a gap has a cause attached.</summary>
    public string? CaptureUnavailableReason { get; init; }

    /// <summary>
    /// Build timestamp of the running service binary.
    ///
    /// Every build reports assembly version 1.0.0.0, so "is the new binary actually installed?"
    /// could only be answered by comparing file timestamps by hand — and when an install silently
    /// failed to replace a locked executable, the old binary kept running and looked identical to
    /// the fix. The agent now states which build is speaking.
    /// </summary>
    public DateTimeOffset? ServiceBuildUtc { get; init; }

    public required int PendingOutbox { get; init; }
    public required int LoopSeconds { get; init; }
    public DateTimeOffset? LastCaptureAtUtc { get; init; }
    public DateTimeOffset? LastFlushAtUtc { get; init; }
    public DateTimeOffset? LastUploadOkAtUtc { get; init; }
}
