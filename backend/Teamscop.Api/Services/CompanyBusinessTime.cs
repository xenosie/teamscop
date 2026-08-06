using Teamscop.Api.Data;
using Teamscop.Engine.Tracking;

namespace Teamscop.Api.Services;

/// <summary>Company-wide business clock helpers (PHASE5).</summary>
public static class CompanyBusinessTime
{
    public static BusinessClockConfig ToConfig(Company c) => new()
    {
        CompanyId = c.Id,
        TimeZoneId = string.IsNullOrWhiteSpace(c.BusinessTimeZoneId) ? "UTC" : c.BusinessTimeZoneId,
        ClockVersion = c.BusinessClockVersion,
        IsSynchronized = c.BusinessClockSynchronized,
        AnchorUtc = c.BusinessAnchorUtc,
        AnchorYear = c.BusinessAnchorYear,
        AnchorMonth = c.BusinessAnchorMonth,
        AnchorDay = c.BusinessAnchorDay,
        AnchorHour = c.BusinessAnchorHour,
        AnchorMinute = c.BusinessAnchorMinute,
        AnchorSecond = c.BusinessAnchorSecond,
        UpdatedAt = c.BusinessClockUpdatedAt ?? c.CreatedAt
    };

    public static BusinessTimestamp At(Company c, DateTimeOffset utc)
    {
        var clock = new BusinessClock();
        clock.Apply(ToConfig(c));
        return clock.At(utc);
    }

    public static void StampEvent(AgentEvent evt, Company company, DateTimeOffset occurredUtc)
    {
        var biz = At(company, occurredUtc);
        evt.BusinessOccurredAt = biz.BusinessLocal;
        evt.BusinessTimeZoneId = biz.TimeZoneId;
        evt.BusinessClockVersion = biz.ClockVersion;
    }
}
