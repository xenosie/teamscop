using System.Globalization;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.Services;

/// <summary>One row of the company-timezone dropdown: the id that gets stored, and its label.</summary>
public sealed record TimeZoneOption(string Id, string Label);

/// <summary>
/// The company clock is a timezone and nothing else (§8.4), so the picker is a flat list
/// of IANA ids ordered by current UTC offset. Windows enumerates its own zone ids, so they
/// are converted to IANA first — the server stores the id verbatim and has to resolve it
/// on Linux.
/// </summary>
public static class TimeZoneCatalog
{
    public static IReadOnlyList<TimeZoneOption> Build()
    {
        var now = DateTime.UtcNow;
        var offsets = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var tz in TimeZoneInfo.GetSystemTimeZones())
            {
                if (ToIanaId(tz.Id) is { } id)
                {
                    offsets[id] = tz.GetUtcOffset(now);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidTimeZoneException or TimeZoneNotFoundException or IOException)
        {
            // A machine with no tzdata still needs a usable picker; it visibly collapses to UTC.
        }

        offsets.TryAdd("UTC", TimeSpan.Zero);

        return offsets
            .OrderBy(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new TimeZoneOption(kv.Key, $"{FormatOffset(kv.Value)} · {kv.Key}"))
            .ToList();
    }

    /// <summary>
    /// Label for an id the catalog does not contain — a company already on a fixed offset
    /// such as UTC+03:00, or a zone this machine does not know. It stays selectable so
    /// opening Settings can never silently move the company somewhere else.
    /// </summary>
    public static TimeZoneOption Describe(string timeZoneId)
    {
        var id = timeZoneId.Trim();
        return BusinessClock.TryResolveTimeZone(id, out var zone)
            ? new TimeZoneOption(id, $"{FormatOffset(zone.GetUtcOffset(DateTime.UtcNow))} · {id}")
            : new TimeZoneOption(id, id);
    }

    public static string FormatOffset(TimeSpan offset)
    {
        var abs = offset.Duration();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"UTC{(offset < TimeSpan.Zero ? '-' : '+')}{abs.Hours:00}:{abs.Minutes:00}");
    }

    private static string? ToIanaId(string systemId)
    {
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(systemId, out var iana)
            && !string.IsNullOrWhiteSpace(iana))
        {
            return iana;
        }

        // Already IANA (Linux/macOS). Legacy single-word aliases (CET, EST, Zulu) are dropped:
        // every region they cover is reachable through a Region/City id.
        return systemId.Contains('/', StringComparison.Ordinal)
               || systemId.Equals("UTC", StringComparison.OrdinalIgnoreCase)
            ? systemId
            : null;
    }
}
