using System.Text.Json;

namespace Teamscop.Engine.Tracking;

/// <summary>
/// §6.3 — persists the last-known company business timezone to
/// <c>%ProgramData%\Teamscop\Agent\business-clock.json</c> so approval codes derive on company-local
/// time even offline. The in-service verifier reads the live <see cref="BusinessClock"/>, but two
/// callers cannot: a service restarted with no network (its clock starts on UTC until the first
/// pull) and, crucially, the separate elevated <c>UninstallGuard.exe</c> launched during removal,
/// which shares no memory with the service. Both read the zone back from here instead.
///
/// Plain JSON, atomic tmp+rename. A timezone id is not a secret — unlike approval material this is
/// deliberately unencrypted. A missing or corrupt file loads as UTC, the same degraded path the
/// live clock already takes for an unresolvable id (§6.6 accepts the resulting near-DST edge).
/// </summary>
public static class BusinessClockStore
{
    public const string FileName = "business-clock.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Write-through on every business-clock change; never throws.</summary>
    public static void Save(string agentRoot, BusinessClockConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        try
        {
            Directory.CreateDirectory(agentRoot);
            var path = Path.Combine(agentRoot, FileName);
            var json = JsonSerializer.Serialize(
                new Persisted { CompanyId = config.CompanyId, TimeZoneId = config.TimeZoneId, UpdatedAt = config.UpdatedAt },
                JsonOptions);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort — the live clock still works this run; an out-of-process guard falls to UTC.
        }
    }

    /// <summary>Last-known zone, or a UTC config when nothing was ever persisted. Never throws.</summary>
    public static BusinessClockConfig Load(string agentRoot)
    {
        try
        {
            var path = Path.Combine(agentRoot, FileName);
            if (File.Exists(path))
            {
                var p = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(path), JsonOptions);
                if (p is not null && !string.IsNullOrWhiteSpace(p.TimeZoneId))
                {
                    return new BusinessClockConfig
                    {
                        CompanyId = p.CompanyId,
                        TimeZoneId = p.TimeZoneId,
                        UpdatedAt = p.UpdatedAt
                    };
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall through to UTC — the safe default.
        }

        return new BusinessClockConfig(); // TimeZoneId defaults to "UTC"
    }

    private sealed class Persisted
    {
        public Guid CompanyId { get; set; }
        public string TimeZoneId { get; set; } = "UTC";
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
