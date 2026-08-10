using Teamscop.Api.Services;
using Teamscop.Engine.Tracking;

namespace Teamscop.Api.Tests;

/// <summary>
/// §2 — one time frame, one conversion, no mismatches.
///
/// The company-local → UTC direction existed twice: the server guarded the spring-forward case,
/// the agent/app copy called TimeZoneInfo.ConvertTimeToUtc directly. That call THROWS on a wall
/// time the transition deletes — and in every zone that springs forward at 00:00 (America/Santiago,
/// America/Havana, America/Asuncion) midnight itself is deleted one day a year. Midnight is exactly
/// what a calendar produces for a day boundary, so selecting that day crashed the app while the
/// server handled it fine. That is the "mismatch on the time concept" this locks out.
/// </summary>
public class CompanyTimeChainTests
{
    /// <summary>Zones whose DST jump lands on midnight, so 00:00 does not exist that day.</summary>
    public static TheoryData<string, int, int, int> MidnightSpringForward => new()
    {
        { "America/Santiago", 2026, 9, 6 },
        { "America/Havana", 2026, 3, 8 },
        { "America/Asuncion", 2026, 10, 4 },
    };

    [Theory]
    [MemberData(nameof(MidnightSpringForward))]
    public void ADayWhoseMidnightDoesNotExistStillResolves(string zoneId, int y, int m, int d)
    {
        if (!CompanyBusinessTime.TryResolve(zoneId, out var zone))
        {
            return; // zone data unavailable on this host — nothing to assert
        }

        var midnight = new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Unspecified);
        if (!zone.IsInvalidTime(midnight))
        {
            return; // tzdata moved the transition; the guard is still exercised by the other rows
        }

        // Neither side may throw, and both must land on the same instant.
        var viaServer = CompanyBusinessTime.ToInstant(midnight, zone);
        var viaAgent = BusinessClock.ToInstant(midnight, zone);

        Assert.Equal(viaServer, viaAgent);
        Assert.True(viaServer > DateTimeOffset.MinValue);
    }

    [Theory]
    [MemberData(nameof(MidnightSpringForward))]
    public void TheAppsCalendarPathAgreesWithTheServersDayBounds(string zoneId, int y, int m, int d)
    {
        if (!CompanyBusinessTime.TryResolve(zoneId, out var zone))
        {
            return;
        }

        var day = new DateOnly(y, m, d);
        var (serverFrom, serverTo) = CompanyBusinessTime.DayBounds(day, zone);

        // What the app computes when the user picks that day in the calendar.
        var cfg = new BusinessClockConfig { TimeZoneId = zoneId };
        var appFrom = BusinessClock.BusinessLocalToUtc(cfg, day.ToDateTime(TimeOnly.MinValue));
        var appTo = BusinessClock.BusinessLocalToUtc(cfg, day.AddDays(1).ToDateTime(TimeOnly.MinValue));

        Assert.Equal(serverFrom, appFrom);
        Assert.Equal(serverTo, appTo);
    }

    [Fact]
    public void ADayIsHalfOpenSoNothingIsDoubleCountedOrLost()
    {
        Assert.True(CompanyBusinessTime.TryResolve("Asia/Tokyo", out var zone));

        var day = new DateOnly(2026, 8, 8);
        var (from, to) = CompanyBusinessTime.DayBounds(day, zone);
        var (nextFrom, _) = CompanyBusinessTime.DayBounds(day.AddDays(1), zone);

        // One day's end is the next day's start: no gap, no overlap.
        Assert.Equal(to, nextFrom);

        var lastMoment = CompanyBusinessTime.ToInstant(
            day.ToDateTime(new TimeOnly(23, 59, 59)), zone);
        Assert.InRange(lastMoment, from, to.AddTicks(-1));
    }

    [Fact]
    public void AFallBackDayIsTwentyFiveHoursAndNeverThrows()
    {
        if (!CompanyBusinessTime.TryResolve("Europe/Berlin", out var zone))
        {
            return;
        }

        // Ambiguous wall times (the hour that happens twice) must resolve deterministically.
        var day = new DateOnly(2026, 10, 25);
        var (from, to) = CompanyBusinessTime.DayBounds(day, zone);

        Assert.Equal(TimeSpan.FromHours(25), to - from);
    }

    [Fact]
    public void PeriodBoundsSpanAnInclusiveDayRange()
    {
        Assert.True(CompanyBusinessTime.TryResolve("Asia/Tokyo", out var zone));

        var start = new DateOnly(2026, 8, 3);
        var end = new DateOnly(2026, 8, 7);
        var (from, to) = CompanyBusinessTime.PeriodBounds(start, end, zone);

        // Inclusive [start..end] means the bar and the query both cover five whole days — this is
        // what makes the timetrack bar span exactly the selected period (§2.5).
        Assert.Equal(CompanyBusinessTime.DayBounds(start, zone).From, from);
        Assert.Equal(CompanyBusinessTime.DayBounds(end, zone).To, to);
        Assert.Equal(TimeSpan.FromDays(5), to - from);
    }
}
