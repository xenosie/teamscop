using Teamscop.Engine.Tracking;
using System.Globalization;

namespace Teamscop.Api.Services;

/// <summary>
/// The whole company clock (§8.4): one timezone id per company, resolved to a
/// <see cref="TimeZoneInfo"/>. There is no anchor and no clock version — company-local
/// time is a pure function of a real UTC instant plus this zone, computed at read time.
/// </summary>
public static class CompanyBusinessTime
{
    /// <summary>
    /// Resolves an IANA id (Europe/Berlin) or a fixed offset (UTC+03:00, +03:00).
    /// Returns false for anything else — callers that persist a zone MUST reject it
    /// rather than silently storing a value that reads back as UTC.
    /// </summary>
    public static bool TryResolve(string? timeZoneId, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return false;
        }

        var id = timeZoneId.Trim();
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            // fall through to fixed-offset parsing
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }

        if (TryParseFixedOffset(id, out var offset))
        {
            zone = TimeZoneInfo.CreateCustomTimeZone(id, offset, id, id);
            return true;
        }

        return false;
    }

    /// <summary>Read path: stored ids are validated on write, so an unknown one degrades to UTC.</summary>
    public static TimeZoneInfo Resolve(string? timeZoneId)
        => TryResolve(timeZoneId, out var zone) ? zone : TimeZoneInfo.Utc;

    /// <summary>
    /// Company-local wall clock for a real instant. Kind is Unspecified on purpose:
    /// it is a clock face, not an instant, and must never masquerade as one (B11).
    /// </summary>
    public static DateTime ToBusinessLocal(DateTimeOffset utc, TimeZoneInfo zone)
        => DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc.UtcDateTime, zone), DateTimeKind.Unspecified);

    public static string ToBusinessLocalIso(DateTimeOffset utc, TimeZoneInfo zone)
        => ToBusinessLocal(utc, zone).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The company-local calendar day that is in progress right now (§8.2).</summary>
    public static DateOnly Today(TimeZoneInfo zone)
        => DateOnly.FromDateTime(ToBusinessLocal(DateTimeOffset.UtcNow, zone));

    /// <summary>
    /// The real instant a company-local wall clock names. A spring-forward transition can delete
    /// the requested time (midnight does not exist everywhere); the next existing instant is used.
    /// </summary>
    /// <summary>
    /// Company-local wall clock → UTC instant. Delegates to <see cref="BusinessClock.ToInstant"/>
    /// so there is exactly ONE implementation of this direction across server, agent and app
    /// (§2.3). Two copies existed and had already diverged — the server's guarded the
    /// spring-forward case and the app's did not, so a calendar day whose midnight does not exist
    /// crashed the app while the server handled it fine.
    /// </summary>
    public static DateTimeOffset ToInstant(DateTime wallClock, TimeZoneInfo zone)
        => BusinessClock.ToInstant(wallClock, zone);

    /// <summary>Half-open UTC bounds of one company-local calendar day: [start, next midnight).</summary>
    public static (DateTimeOffset From, DateTimeOffset To) DayBounds(DateOnly day, TimeZoneInfo zone)
        => (ToInstant(day.ToDateTime(TimeOnly.MinValue), zone),
            ToInstant(day.AddDays(1).ToDateTime(TimeOnly.MinValue), zone));

    /// <summary>
    /// Half-open UTC bounds of an <b>inclusive</b> company-local day range [<paramref name="fromDay"/> ..
    /// <paramref name="toDay"/>]: [start of fromDay, start of the day after toDay). This is the ONE
    /// conversion every period endpoint routes through (§2.3), so a calendar selection and the query
    /// it produces are the same two instants and can never disagree — including across a DST
    /// transition, because both ends run the guarded <see cref="ToInstant"/>.
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset To) PeriodBounds(DateOnly fromDay, DateOnly toDay, TimeZoneInfo zone)
        => (DayBounds(fromDay, zone).From, DayBounds(toDay, zone).To);

    /// <summary>
    /// True when <paramref name="value"/> is a bare company-local calendar day (<c>yyyy-MM-dd</c>) —
    /// what the admin's calendar sends (§2.2). A full timestamp is an absolute instant, not a day,
    /// and returns false so the caller passes it through unconverted.
    /// </summary>
    public static bool TryParseCompanyDay(string? value, out DateOnly day)
        => DateOnly.TryParseExact(
            (value ?? string.Empty).Trim(),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out day);

    private static bool TryParseFixedOffset(string value, out TimeSpan offset)
    {
        offset = default;
        var s = value.ToUpperInvariant().Replace("UTC", "", StringComparison.Ordinal).Trim();
        if (s.Length == 0)
        {
            offset = TimeSpan.Zero;
            return true;
        }

        var negative = s.StartsWith('-');
        s = s.TrimStart('+', '-');
        var parts = s.Split(':');
        if (parts.Length < 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            return false;
        }

        var seconds = 0;
        if (parts.Length >= 3
            && !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
        {
            return false;
        }

        var candidate = new TimeSpan(hours, minutes, seconds);
        if (candidate > TimeSpan.FromHours(14) || candidate < TimeSpan.Zero)
        {
            return false;
        }

        offset = negative ? -candidate : candidate;
        return true;
    }
}
