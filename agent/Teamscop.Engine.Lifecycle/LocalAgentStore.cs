using System.Text.Json;

namespace Teamscop.Engine.Lifecycle;

public sealed class LocalAgentState
{
    public string? AccessToken { get; set; }
    public string? DeviceKey { get; set; }
    public string? Role { get; set; }
    public Guid? CompanyId { get; set; }
    public string? ApiBaseUrl { get; set; }
    public string? Username { get; set; }
    public string? CompanyName { get; set; }
    public string? CompanyAvatarUrl { get; set; }
}

/// <summary>
/// Persists agent session under ProgramData (staff) or LocalAppData (admin).
/// </summary>
public sealed class LocalAgentStore
{
    private readonly string _path;

    public LocalAgentStore(AgentRole role, string? overrideDirectory = null)
    {
        var root = overrideDirectory ?? ResolveRoot(role);
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "agent-state.json");
    }

    public string StatePath => _path;

    public LocalAgentState Load()
    {
        if (!File.Exists(_path))
        {
            return new LocalAgentState();
        }

        var json = File.ReadAllText(_path);
        return JsonSerializer.Deserialize<LocalAgentState>(json) ?? new LocalAgentState();
    }

    public void Save(LocalAgentState state)
    {
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }

    private static string ResolveRoot(AgentRole role)
    {
        if (role == AgentRole.Staff)
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "Teamscop", "Agent");
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "Teamscop", "Admin");
    }
}
