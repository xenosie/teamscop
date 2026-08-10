using System.Globalization;
using System.Text.Json.Serialization;

namespace Teamscop.Engine.Tracking;

/// <summary>
/// Company business clock (§8.3, §8.4): one timezone per company, nothing more.
/// Company-local time is a pure function of a real UTC instant plus this zone.
/// The absolute anchor (declare an exact wall time, then run localAnchor + elapsed)
/// is deleted — the server no longer stores or sends it.
/// </summary>
public sealed class BusinessClockConfig
{
    public Guid CompanyId { get; set; }
    public string TimeZoneId { get; set; } = "UTC";

    // ---- Anchor remnants: no longer sent by the server, no longer read by this clock.
    // They survive only because Teamscop.App (SettingsViewModel) and Teamscop.StaffService
    // (StaffAgentWorker) still reference them; both are outside this domain. They now
    // always deserialize to their defaults, which is what selects the plain-timezone path.
    public long ClockVersion { get; set; }
    public bool IsSynchronized { get; set; }

    public int? AnchorYear { get; set; }
    public int? AnchorMonth { get; set; }
    public int? AnchorDay { get; set; }
    public int? AnchorHour { get; set; }
    public int? AnchorMinute { get; set; }
    public int? AnchorSecond { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public DateTime? AnchorBusinessLocal =>
        AnchorYear is int y && AnchorMonth is int m && AnchorDay is int d
            ? new DateTime(y, m, d, AnchorHour ?? 0, AnchorMinute ?? 0, AnchorSecond ?? 0, DateTimeKind.Unspecified)
            : null;
}

public sealed class BusinessTimestamp
{
    public required DateTimeOffset Utc { get; init; }
    public required DateTime BusinessLocal { get; init; }
    public required string BusinessLocalIso { get; init; }
    public required string TimeZoneId { get; init; }

    // Anchor remnants, always 0 / false now. Still stamped into outbox envelopes and
    // still read by Teamscop.StaffService, which is outside this domain.
    public required long ClockVersion { get; init; }
    public required bool Synchronized { get; init; }
}

public sealed class BusinessClock
{
    private BusinessClockConfig _config = new();
    private readonly object _gate = new();

    public BusinessClockConfig Config
    {
        get { lock (_gate) return Clone(_config); }
    }

    public void Apply(BusinessClockConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate)
        {
            _config = Clone(config);
        }
    }

    public BusinessTimestamp Now()
        => At(DateTimeOffset.UtcNow);

    public BusinessTimestamp At(DateTimeOffset utc)
    {
        BusinessClockConfig cfg;
        lock (_gate) cfg = _config;

        // Read path: the server validates the id on write, so an id this agent cannot
        // resolve degrades to UTC rather than stopping capture. Kind is Unspecified on
        // purpose — this is a clock face, not an instant, and must never pose as one.
        var tz = TryResolveTimeZone(cfg.TimeZoneId, out var resolved) ? resolved : TimeZoneInfo.Utc;
        var businessLocal = DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(utc.UtcDateTime, tz), DateTimeKind.Unspecified);

        return new BusinessTimestamp
        {
            Utc = utc,
            BusinessLocal = businessLocal,
            BusinessLocalIso = businessLocal.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            TimeZoneId = cfg.TimeZoneId,
            ClockVersion = cfg.ClockVersion,
            Synchronized = cfg.IsSynchronized
        };
    }

    /// <summary>
    /// Convert a company business-local wall time (Unspecified) back to a UTC instant.
    /// </summary>
    public static DateTimeOffset BusinessLocalToUtc(BusinessClockConfig cfg, DateTime businessLocal)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var tz = TryResolveTimeZone(cfg.TimeZoneId, out var resolved) ? resolved : TimeZoneInfo.Utc;
        return ToInstant(businessLocal, tz);
    }

    /// <summary>
    /// Company-local wall clock → UTC instant. **The single implementation** of this direction in
    /// the product (§2.3); the server's <c>CompanyBusinessTime.ToInstant</c> delegates here, so the
    /// calendar, the period query and the timetrack bar cannot disagree.
    ///
    /// Guarded on purpose. A spring-forward transition makes some wall times non-existent, and
    /// <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/> THROWS on those. In every
    /// zone that springs forward at 00:00 — America/Santiago, America/Havana, America/Asuncion —
    /// midnight itself is invalid one day a year, which is precisely the value a calendar produces
    /// for a day boundary. Unguarded, selecting that day crashed the app.
    ///
    /// Invalid times advance to the first valid instant (the skipped hour simply does not exist, so
    /// that day is 23h). Ambiguous times during a fall-back use the framework's standard-offset
    /// rule, which is deterministic. Never throws.
    /// </summary>
    public static DateTimeOffset ToInstant(DateTime wallClock, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(15);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    /// <summary>
    /// Resolves an IANA id (Europe/Berlin) or a fixed offset (UTC+03:00, +03:00).
    /// Returns false for anything else. Callers that are about to PERSIST a zone must
    /// use this (or <see cref="ResolveTimeZone"/>) and reject a false — silently storing
    /// an unresolvable id is how a company ends up on UTC without anyone noticing.
    /// </summary>
    public static bool TryResolveTimeZone(string? timeZoneId, out TimeZoneInfo zone)
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

    /// <summary>
    /// Strict resolve: throws on an id this machine cannot resolve.
    /// The old silent UTC fallback made every "validate the zone the admin picked"
    /// call site a no-op, so typos were stored and read back as UTC.
    /// </summary>
    /// <exception cref="TimeZoneNotFoundException">The id is neither a known zone nor a fixed offset.</exception>
    public static TimeZoneInfo ResolveTimeZone(string timeZoneId)
        => TryResolveTimeZone(timeZoneId, out var zone)
            ? zone
            : throw new TimeZoneNotFoundException($"Unknown time zone id: '{timeZoneId}'.");

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

        // Same bounds the server enforces: no real zone is beyond ±14:00.
        var candidate = new TimeSpan(hours, minutes, seconds);
        if (candidate > TimeSpan.FromHours(14) || candidate < TimeSpan.Zero)
        {
            return false;
        }

        offset = negative ? -candidate : candidate;
        return true;
    }

    private static BusinessClockConfig Clone(BusinessClockConfig c) => new()
    {
        CompanyId = c.CompanyId,
        TimeZoneId = c.TimeZoneId,
        ClockVersion = c.ClockVersion,
        IsSynchronized = c.IsSynchronized,
        AnchorYear = c.AnchorYear,
        AnchorMonth = c.AnchorMonth,
        AnchorDay = c.AnchorDay,
        AnchorHour = c.AnchorHour,
        AnchorMinute = c.AnchorMinute,
        AnchorSecond = c.AnchorSecond,
        UpdatedAt = c.UpdatedAt
    };
}
