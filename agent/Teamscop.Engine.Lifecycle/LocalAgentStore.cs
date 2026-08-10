using System.Text.Json;

namespace Teamscop.Engine.Lifecycle;

public sealed class LocalAgentState
{
    public string? AccessToken { get; set; }
    /// <summary>DPAPI-protected access token (Windows LocalMachine). Preferred over cleartext.</summary>
    public string? AccessTokenProtected { get; set; }
    public string? DeviceKey { get; set; }
    public string? Role { get; set; }
    public Guid? UserId { get; set; }
    public Guid? CompanyId { get; set; }
    public string? ApiBaseUrl { get; set; }
    public string? Username { get; set; }
    public string? CompanyName { get; set; }
    public string? CompanyAvatarUrl { get; set; }
}

/// <summary>
/// Persists agent session under ProgramData (staff) or LocalAppData (admin).
/// On Windows staff path, AccessToken is DPAPI LocalMachine protected.
/// </summary>
public sealed class LocalAgentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly bool _protectToken;

    public LocalAgentStore(AgentRole role, string? overrideDirectory = null)
    {
        var root = overrideDirectory ?? ResolveRoot(role);
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "agent-state.json");
        _protectToken = role == AgentRole.Staff && OperatingSystem.IsWindows();
    }

    public string StatePath => _path;

    public LocalAgentState Load()
    {
        if (!File.Exists(_path))
        {
            return new LocalAgentState();
        }

        try
        {
            var json = File.ReadAllText(_path);
            var state = JsonSerializer.Deserialize<LocalAgentState>(json) ?? new LocalAgentState();
            if (!string.IsNullOrWhiteSpace(state.AccessTokenProtected))
            {
                state.AccessToken = Dpapi.UnprotectFromBase64(state.AccessTokenProtected) ?? state.AccessToken;
            }

            return state;
        }
        catch
        {
            return new LocalAgentState();
        }
    }

    public void Save(LocalAgentState state)
    {
        var toWrite = new LocalAgentState
        {
            DeviceKey = state.DeviceKey,
            Role = state.Role,
            UserId = state.UserId,
            CompanyId = state.CompanyId,
            ApiBaseUrl = state.ApiBaseUrl,
            Username = state.Username,
            CompanyName = state.CompanyName,
            CompanyAvatarUrl = state.CompanyAvatarUrl
        };

        // DPAPI failure must not cost the machine its enrolment: fall back to cleartext under the
        // SYSTEM+Administrators DACL the installer applies, rather than writing nothing at all.
        var sealedToken = _protectToken && !string.IsNullOrWhiteSpace(state.AccessToken)
            ? Dpapi.ProtectToBase64(state.AccessToken)
            : null;
        toWrite.AccessTokenProtected = sealedToken;
        toWrite.AccessToken = sealedToken is null ? state.AccessToken : null;

        var json = JsonSerializer.Serialize(toWrite, JsonOptions);
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>
    /// Where the staff session token lives. Deliberately NOT <c>Teamscop\Agent</c>.
    ///
    /// <c>Agent</c> holds the vault and the outbox, so its ACL grants only SYSTEM and
    /// Administrators — the monitored employee must not read captured data. But the process that
    /// WRITES this file is Teamscop.App, which is asInvoker: on a standard account, and on an
    /// administrator's UAC-filtered token, it has no write access there at all. Joining a company
    /// therefore appeared to succeed and silently persisted nothing, so the enrolment was lost on
    /// the next close or reboot.
    ///
    /// The token is the employee's own credential, not captured data, so it lives in a sibling
    /// directory the interactive user may write while <c>Agent</c> stays locked.
    /// </summary>
    public const string StaffSessionDirectory = "Session";

    private static string ResolveRoot(AgentRole role)
    {
        if (role == AgentRole.Staff)
        {
            if (OperatingSystem.IsWindows())
            {
                var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                return Path.Combine(programData, "Teamscop", StaffSessionDirectory);
            }

            var staffLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(staffLocal, "Teamscop", "Staff");
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "Teamscop", "Admin");
    }
}
