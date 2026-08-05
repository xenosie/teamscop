using Teamscop.Engine.Sync;
using Teamscop.Engine.Tracking;

namespace Teamscop.Api.Tests;

public class AppHistoryFilterTests
{
    [Theory]
    [InlineData(AgentEventTypes.Registration, true)]
    [InlineData(AgentEventTypes.PowerOff, true)]
    [InlineData(AgentEventTypes.UsbEvent, true)]
    [InlineData(AgentEventTypes.Uninstall, true)]
    [InlineData(AgentEventTypes.AppBroken, true)]
    [InlineData(AgentEventTypes.Heartbeat, false)]
    [InlineData(AgentEventTypes.TimeTrack, false)]
    [InlineData(null, false)]
    public void IsAppHistory_Filters_Phase9_Types(string? type, bool expected)
        => Assert.Equal(expected, AppHistoryEventTypes.IsAppHistory(type));
}
