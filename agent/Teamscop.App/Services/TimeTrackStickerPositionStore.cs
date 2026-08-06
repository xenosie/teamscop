using System.Text.Json;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.Services;

public sealed class TimeTrackStickerPosition
{
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>Persists sticker screen position next to the staff agent state.</summary>
public static class TimeTrackStickerPositionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string ResolvePath(AgentRole role = AgentRole.Staff)
    {
        var statePath = new LocalAgentStore(role).StatePath;
        var dir = Path.GetDirectoryName(statePath) ?? ".";
        return Path.Combine(dir, "timetrack-sticker.json");
    }

    public static TimeTrackStickerPosition? Load(AgentRole role = AgentRole.Staff)
    {
        var path = ResolvePath(role);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TimeTrackStickerPosition>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(double x, double y, AgentRole role = AgentRole.Staff)
    {
        try
        {
            var path = ResolvePath(role);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(new TimeTrackStickerPosition { X = x, Y = y }, JsonOptions));
        }
        catch
        {
            // best-effort
        }
    }
}
