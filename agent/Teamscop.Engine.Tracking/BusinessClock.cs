using System.Globalization;
using System.Text.Json.Serialization;

namespace Teamscop.Engine.Tracking;

/// <summary>
/// Company-wide synchronized business clock.
/// Admin declares an absolute local wall time; from that UTC instant forward,
/// all agents compute the same business timeline: localAnchor + (utcNow - utcAnchor).
/// </summary>
public sealed class BusinessClockConfig
{
    public Guid CompanyId { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public long ClockVersion { get; set; }
    public bool IsSynchronized { get; set; }

    /// <summary>UTC instant when admin declared the sync.</summary>
    public DateTimeOffset? AnchorUtc { get; set; }

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

        DateTime businessLocal;
        if (cfg is { IsSynchronized: true, AnchorUtc: { } anchorUtc } && cfg.AnchorBusinessLocal is { } anchorLocal)
        {
            // Locked synchronized timeline shared by every staff machine.
            businessLocal = anchorLocal + (utc - anchorUtc);
        }
        else
        {
            var tz = ResolveTimeZone(cfg.TimeZoneId);
            businessLocal = TimeZoneInfo.ConvertTimeFromUtc(utc.UtcDateTime, tz);
        }

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
    /// Convert a company business-local wall time (Unspecified) back to UTC using the sync formula.
    /// </summary>
    public static DateTimeOffset BusinessLocalToUtc(BusinessClockConfig cfg, DateTime businessLocal)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var local = DateTime.SpecifyKind(businessLocal, DateTimeKind.Unspecified);

        if (cfg is { IsSynchronized: true, AnchorUtc: { } anchorUtc } && cfg.AnchorBusinessLocal is { } anchorLocal)
        {
            return anchorUtc + (local - anchorLocal);
        }

        var tz = ResolveTimeZone(cfg.TimeZoneId);
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, tz);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    public static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            // Accept fixed offsets like UTC+03:00 / +03:00
            if (TryParseFixedOffset(timeZoneId, out var offset))
            {
                return TimeZoneInfo.CreateCustomTimeZone(timeZoneId, offset, timeZoneId, timeZoneId);
            }

            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static bool TryParseFixedOffset(string value, out TimeSpan offset)
    {
        offset = default;
        var s = value.Trim().ToUpperInvariant().Replace("UTC", "", StringComparison.Ordinal).Trim();
        if (s.Length == 0)
        {
            offset = TimeSpan.Zero;
            return true;
        }

        var negative = s.StartsWith('-');
        s = s.TrimStart('+', '-');
        var parts = s.Split(':');
        if (parts.Length >= 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            var seconds = 0;
            if (parts.Length >= 3)
            {
                _ = int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds);
            }

            offset = new TimeSpan(hours, minutes, seconds);
            if (negative)
            {
                offset = -offset;
            }

            return true;
        }

        return false;
    }

    private static BusinessClockConfig Clone(BusinessClockConfig c) => new()
    {
        CompanyId = c.CompanyId,
        TimeZoneId = c.TimeZoneId,
        ClockVersion = c.ClockVersion,
        IsSynchronized = c.IsSynchronized,
        AnchorUtc = c.AnchorUtc,
        AnchorYear = c.AnchorYear,
        AnchorMonth = c.AnchorMonth,
        AnchorDay = c.AnchorDay,
        AnchorHour = c.AnchorHour,
        AnchorMinute = c.AnchorMinute,
        AnchorSecond = c.AnchorSecond,
        UpdatedAt = c.UpdatedAt
    };
}
