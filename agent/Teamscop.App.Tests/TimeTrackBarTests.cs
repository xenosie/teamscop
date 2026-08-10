using Avalonia.Media;
using Teamscop.App.Services;
using Teamscop.App.ViewModels;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.Tests;

/// <summary>
/// §2.5 — the timetrack bar. It was empty because a concrete period was never selected once the
/// Today drill-down (its only period-applying entry) was deleted (§1.3); a staff member now opens on
/// today's period. The bar is two colours (green working / red everything-else, §5.4) with a "now"
/// marker positioned against the server's period bounds.
/// </summary>
public class TimeTrackBarTests
{
    [Fact]
    public void EnsureDefaultPeriod_SeedsTodaySilently_WhenNothingIsApplied()
        => AppTestHost.Run(() =>
        {
            using var host = TestServices.SignedIn(new StubApi());
            var filter = new StaffPeriodFilterViewModel(host.Services);
            var raised = false;
            filter.FilterChanged += () => raised = true;

            Assert.Null(filter.AppliedStart);

            filter.EnsureDefaultPeriod();

            Assert.Equal(host.Services.Clock.Today, filter.AppliedStart);
            Assert.NotNull(filter.AppliedFromUtc);
            Assert.NotNull(filter.AppliedToUtc);
            Assert.False(raised, "the default is seeded silently — the caller triggers the one reload");
            return Task.CompletedTask;
        });

    [Fact]
    public void EnsureDefaultPeriod_KeepsAPeriodTheUserAlreadyChose()
        => AppTestHost.Run(() =>
        {
            using var host = TestServices.SignedIn(new StubApi());
            var filter = new StaffPeriodFilterViewModel(host.Services);
            filter.ApplyRange(new DateTime(2026, 1, 5), new DateTime(2026, 1, 7));
            var chosenFrom = filter.AppliedFromUtc;

            filter.EnsureDefaultPeriod();

            Assert.Equal(new DateTime(2026, 1, 5), filter.AppliedStart);
            Assert.Equal(chosenFrom, filter.AppliedFromUtc);
        return Task.CompletedTask;
        });

    [Fact]
    public void TheBar_PaintsTheNowMarker_AndRendersEverythingNonWorkingAsRed()
        => AppTestHost.Run(async () =>
        {
            var now = DateTimeOffset.UtcNow;
            var api = new StubApi
            {
                Timeline = new TimeTrackTimeline
                {
                    From = now.AddHours(-1),
                    To = now.AddHours(1),
                    TotalSeconds = 7200,
                    Segments =
                    [
                        new TimeTrackSegmentItem
                            { Kind = "working", Start = now.AddHours(-1), End = now, DurationSeconds = 3600 },
                        new TimeTrackSegmentItem
                            { Kind = "gap", Start = now, End = now.AddHours(1), DurationSeconds = 3600 }
                    ]
                }
            };
            using var host = TestServices.SignedIn(api);
            var vm = new TimeTrackViewModel(host.Services);
            var day = host.Services.Clock.Today;

            await vm.LoadAsync(Guid.NewGuid(), force: true, api.Timeline.From, api.Timeline.To, day, day);

            Assert.True(vm.HasTimeline);
            Assert.True(vm.HasNowMarker);
            Assert.InRange(vm.NowFraction, 0.45, 0.55);

            // §5.4 — a "gap" segment renders red and reads as rest, not a third "no data" state.
            var gap = vm.Segments.Last();
            Assert.Equal("gap", gap.Kind);
            Assert.Equal("Rest", gap.KindLabel);
            Assert.Equal("#FFDC2626", ((SolidColorBrush)gap.Fill).Color.ToString());
            Assert.Equal("#FF16A34A", ((SolidColorBrush)vm.Segments[0].Fill).Color.ToString());

            // Rest total folds the gap in, so working and rest are each one hour.
            Assert.Equal(CompanyClock.FormatDuration(3600), vm.WorkingSummary);
            Assert.Equal(CompanyClock.FormatDuration(3600), vm.RestSummary);
        });

    [Fact]
    public void TheNowMarker_IsAbsentForAPeriodThatDoesNotContainNow()
        => AppTestHost.Run(async () =>
        {
            var now = DateTimeOffset.UtcNow;
            var api = new StubApi
            {
                Timeline = new TimeTrackTimeline
                {
                    From = now.AddHours(-3),
                    To = now.AddHours(-1),
                    TotalSeconds = 7200,
                    Segments =
                    [
                        new TimeTrackSegmentItem
                            { Kind = "working", Start = now.AddHours(-3), End = now.AddHours(-1), DurationSeconds = 7200 }
                    ]
                }
            };
            using var host = TestServices.SignedIn(api);
            var vm = new TimeTrackViewModel(host.Services);
            var day = host.Services.Clock.Today;

            await vm.LoadAsync(Guid.NewGuid(), force: true, api.Timeline.From, api.Timeline.To, day, day);

            Assert.True(vm.HasTimeline);
            Assert.False(vm.HasNowMarker);
        });
}
