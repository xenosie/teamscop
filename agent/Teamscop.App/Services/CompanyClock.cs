using System.Globalization;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.Services;

/// <summary>
/// The app's only time formatter. Every view renders company time and nothing else (§8.2), so
/// the company timezone lives here once instead of in each view's private FormatSpan.
/// </summary>
public sealed class CompanyClock
{
    private readonly BusinessClock _clock = new();
    private readonly object _gate = new();
    private BusinessClockConfig _config = new();

    /// <summary>Raised after the company zone changes — views re-render their labels.</summary>
    public event Action? Changed;

    /// <summary>
    /// Persists the zone for the uninstall guard, which runs with no session and no network.
    ///
    /// Approval codes step on COMPANY-local time. The guard used to depend on business-clock.json
    /// written only by the service — so on any machine where the service had not run, it fell back
    /// to UTC and every correct code was refused (a 9-hour company offset is far outside the ±1-step
    /// window), with each refusal feeding the lockout. The app knows the zone the moment it signs
    /// in, so it writes the same file the guard reads.
    /// </summary>
    public Action<BusinessClockConfig>? Persist { get; set; }

    public BusinessClockConfig Config
    {
        get { lock (_gate) return _config; }
    }

    public string TimeZoneId
    {
        get { lock (_gate) return string.IsNullOrWhiteSpace(_config.TimeZoneId) ? "UTC" : _config.TimeZoneId; }
    }

    /// <summary>"UTC+02:00 · Europe/Warsaw" — the label §8.4's dropdown shows.</summary>
    public string ZoneLabel => TimeZoneCatalog.Describe(TimeZoneId).Label;

    /// <summary>Company-local wall clock right now. Unspecified kind on purpose: it is a clock face.</summary>
    public DateTime Now => _clock.Now().BusinessLocal;

    /// <summary>Company-local calendar day right now — the unit every period filter works in.</summary>
    public DateTime Today => Now.Date;

    public void Apply(BusinessClockConfig config)
    {
        bool zoneChanged;
        lock (_gate)
        {
            zoneChanged = !string.Equals(_config.TimeZoneId, config.TimeZoneId, StringComparison.Ordinal);
            _config = config;
            _clock.Apply(config);
        }

        if (zoneChanged)
        {
            Changed?.Invoke();
        }

        try
        {
            Persist?.Invoke(config);
        }
        catch
        {
            // Best effort: the service also writes this file whenever it runs.
        }
    }

    public DateTime ToCompany(DateTimeOffset utc) => _clock.At(utc).BusinessLocal;

    public DateTimeOffset ToUtc(DateTime companyLocal) => BusinessClock.BusinessLocalToUtc(Config, companyLocal);

    /// <summary>"2026-08-07 14:32".</summary>
    public string FormatDateTime(DateTimeOffset utc)
        => ToCompany(utc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"14:32".</summary>
    public string FormatTime(DateTimeOffset utc)
        => ToCompany(utc).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"07 Aug 2026".</summary>
    public static string FormatDay(DateTime companyLocal)
        => companyLocal.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>"07 Aug 2026" for one day, "05 Aug 2026 – 07 Aug 2026" for a span.</summary>
    public static string FormatDayRange(DateTime start, DateTime end)
        => start.Date == end.Date ? FormatDay(start) : $"{FormatDay(start)} – {FormatDay(end)}";

    /// <summary>Server-projected business time when present, then the agent's stamp, then this zone.</summary>
    public string FormatEventTime(TrackingEventItem evt) => BusinessTimeDisplay.FormatEventTime(evt, _clock);

    /// <summary>"4h 12m" / "12m" / "0m" — the compact form used in bars and tiles.</summary>
    public static string FormatDuration(double seconds)
    {
        if (seconds < 1)
        {
            return "0m";
        }

        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))}m";
    }

    /// <summary>"4 hours 12 minutes" — the long form used in sentences.</summary>
    public static string FormatDurationLong(double seconds)
    {
        if (seconds < 1)
        {
            return "0 minutes";
        }

        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalHours >= 1)
        {
            var hours = (int)span.TotalHours;
            var minutes = span.Minutes;
            var hoursText = $"{hours} hour{(hours == 1 ? "" : "s")}";
            return minutes > 0
                ? $"{hoursText} {minutes} minute{(minutes == 1 ? "" : "s")}"
                : hoursText;
        }

        var mins = Math.Max(1, (int)Math.Round(span.TotalMinutes));
        return $"{mins} minute{(mins == 1 ? "" : "s")}";
    }
}
