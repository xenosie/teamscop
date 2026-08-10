using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Tests;

/// <summary>
/// §4.2 — subdomains roll up under the registrable domain for the LIST, but the full URLs are still
/// stored and shown when a domain is opened. The rollup is server-side and derived from the URL, so
/// it cannot be defeated by whatever the agent put in a payload Domain field.
/// </summary>
public sealed class BrowsingRollupFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly TrackingScenario _api;

    public BrowsingRollupFlowTests(WebApplicationFactory<Program> factory)
        => _api = new TrackingScenario(factory.WithWebHostBuilder(_ => { }).CreateClient());

    [Fact]
    public async Task Subdomains_CollapseUnderRegistrableDomain_ForTheList_ButDetailKeepsFullUrls()
    {
        var (_, adminToken, companyToken) = await _api.SignupAdminAsync("Browse Co");
        var (staffId, staffToken) = await _api.SignupStaffAsync(companyToken, "Surfer");

        var now = DateTimeOffset.UtcNow;
        await _api.IngestAsync(staffToken, AgentEventTypes.BrowserHistory, now, JsonSerializer.Serialize(new
        {
            visits = new[]
            {
                new { url = "https://copilot.github.com/chat", title = "Copilot", visitedAt = now.AddMinutes(-3), visitId = 1 },
                new { url = "https://github.com/torvalds/linux", title = "Linux", visitedAt = now.AddMinutes(-2), visitId = 2 },
                new { url = "https://www.bbc.co.uk/news", title = "BBC", visitedAt = now.AddMinutes(-1), visitId = 3 }
            }
        }));

        using var domains = await _api.GetJsonAsync(
            $"/api/tracking/browsing?staffUserId={staffId:D}", adminToken);
        var rows = domains.RootElement.EnumerateArray().ToList();

        // github.com and copilot.github.com collapse to one registrable-domain row of two visits.
        var github = Assert.Single(rows, r => r.GetProperty("domain").GetString() == "https://github.com");
        Assert.Equal(2, github.GetProperty("visitCount").GetInt32());

        // A multi-part public suffix (co.uk) is NOT over-collapsed to co.uk.
        Assert.Contains(rows, r => r.GetProperty("domain").GetString() == "https://bbc.co.uk");
        Assert.DoesNotContain(rows, r => r.GetProperty("domain").GetString() == "https://co.uk");

        // Drill in: the full URLs survive.
        using var detail = await _api.GetJsonAsync(
            $"/api/tracking/browsing/detail?staffUserId={staffId:D}&domain=github.com", adminToken);
        var urls = detail.RootElement.GetProperty("visits").EnumerateArray()
            .Select(v => v.GetProperty("url").GetString())
            .ToList();
        Assert.Contains("https://copilot.github.com/chat", urls);
        Assert.Contains("https://github.com/torvalds/linux", urls);
    }
}
