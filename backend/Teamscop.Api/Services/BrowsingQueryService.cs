using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;
using Teamscop.Api.Services.Access;
using Teamscop.Api.Services.Insights;
using Teamscop.Engine.Sync;
using Teamscop.Engine.Tracking;

namespace Teamscop.Api.Services;

public sealed class BrowsingDomainSummaryDto
{
    public string Domain { get; set; } = "";
    public int VisitCount { get; set; }
    public DateTimeOffset? LastVisitedAt { get; set; }
}

public sealed class BrowsingVisitDto
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Profile { get; set; } = "";
    public DateTimeOffset VisitedAt { get; set; }
    public long? VisitId { get; set; }
}

public sealed class BrowsingDomainDetailDto
{
    public string Domain { get; set; } = "";
    public int VisitCount { get; set; }
    public List<BrowsingVisitDto> Visits { get; set; } = [];
}

public sealed class BrowsingTopUrlDto
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public int VisitCount { get; set; }
    public DateTimeOffset? LastVisitedAt { get; set; }
}

public interface IBrowsingQueryService
{
    Task<IReadOnlyList<BrowsingDomainSummaryDto>> ListDomainsAsync(
        Guid viewerId,
        Guid staffUserId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int takeEvents,
        CancellationToken ct);

    Task<BrowsingDomainDetailDto?> GetDomainDetailAsync(
        Guid viewerId,
        Guid staffUserId,
        string domain,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int takeEvents,
        CancellationToken ct);

    Task<IReadOnlyList<BrowsingTopUrlDto>> ListTopUrlsAsync(
        Guid viewerId,
        Guid staffUserId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int takeUrls,
        int takeEvents,
        CancellationToken ct);
}

public sealed class BrowsingQueryService(
    AppDbContext db,
    IStaffDataGuard guard) : IBrowsingQueryService
{
    public async Task<IReadOnlyList<BrowsingDomainSummaryDto>> ListDomainsAsync(
        Guid viewerId,
        Guid staffUserId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int takeEvents,
        CancellationToken ct)
    {
        await RequireAsync(viewerId, staffUserId, ct);
        var visits = await LoadVisitsAsync(staffUserId, from, to, takeEvents, ct);
        return visits
            .GroupBy(v => v.Domain, StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .Select(g => new BrowsingDomainSummaryDto
            {
                Domain = g.Key,
                VisitCount = g.Count(),
                LastVisitedAt = g.Max(x => x.VisitedAt)
            })
            .OrderByDescending(x => x.VisitCount)
            .ThenBy(x => x.Domain, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<BrowsingDomainDetailDto?> GetDomainDetailAsync(
        Guid viewerId,
        Guid staffUserId,
        string domain,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int takeEvents,
        CancellationToken ct)
    {
        await RequireAsync(viewerId, staffUserId, ct);
        var normalized = NormalizeDomainKey(domain);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var visits = await LoadVisitsAsync(staffUserId, from, to, takeEvents, ct);
        var matched = visits
            .Where(v => string.Equals(v.Domain, normalized, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.VisitedAt)
            .Select(v => new BrowsingVisitDto
            {
                Url = v.Url,
                Title = v.Title,
                Profile = v.Profile,
                VisitedAt = v.VisitedAt,
                VisitId = v.VisitId
            })
            .ToList();

        return new BrowsingDomainDetailDto
        {
            Domain = normalized,
            VisitCount = matched.Count,
            Visits = matched
        };
    }

    public async Task<IReadOnlyList<BrowsingTopUrlDto>> ListTopUrlsAsync(
        Guid viewerId,
        Guid staffUserId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int takeUrls,
        int takeEvents,
        CancellationToken ct)
    {
        await RequireAsync(viewerId, staffUserId, ct);
        takeUrls = Math.Clamp(takeUrls <= 0 ? 3 : takeUrls, 1, 20);
        var visits = await LoadVisitsAsync(staffUserId, from, to, takeEvents, ct);
        return visits
            .Where(v => !string.IsNullOrWhiteSpace(v.Url))
            .GroupBy(v => v.Url.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var latest = g.OrderByDescending(x => x.VisitedAt).First();
                return new BrowsingTopUrlDto
                {
                    Url = g.Key,
                    Title = latest.Title,
                    VisitCount = g.Count(),
                    LastVisitedAt = g.Max(x => x.VisitedAt)
                };
            })
            .OrderByDescending(x => x.VisitCount)
            .ThenByDescending(x => x.LastVisitedAt)
            .Take(takeUrls)
            .ToList();
    }

    private Task RequireAsync(Guid viewerId, Guid staffUserId, CancellationToken ct)
        => guard.RequireViewableAsync(viewerId, staffUserId, AgentEventTypes.BrowserHistory, ct);

    private async Task<List<ParsedVisit>> LoadVisitsAsync(
        Guid staffUserId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int takeEvents,
        CancellationToken ct)
    {
        takeEvents = Math.Clamp(takeEvents <= 0 ? 200 : takeEvents, 1, 500);
        var rows = await db.AgentEvents.AsNoTracking()
            .ForStaff(staffUserId)
            .OfType(AgentEventTypes.BrowserHistory)
            .InPeriod(from, to)
            .Newest(takeEvents)
            .Select(e => e.PayloadJson)
            .ToListAsync(ct);

        var visits = new List<ParsedVisit>();
        foreach (var payload in rows)
        {
            visits.AddRange(ParseVisits(payload));
        }

        // Deduplicate identical VisitId+Url within the window when re-sent.
        return visits
            .GroupBy(v => (v.VisitId, v.Url), v => v)
            .Select(g => g.OrderByDescending(x => x.VisitedAt).First())
            .ToList();
    }

    private static IEnumerable<ParsedVisit> ParseVisits(string payloadJson)
    {
        using var innerDoc = AgentEventPayload.TryOpen(payloadJson, "visits");
        if (innerDoc is null
            || !innerDoc.RootElement.TryGetProperty("visits", out var visits)
            || visits.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var v in visits.EnumerateArray())
        {
            var url = AgentEventPayload.String(v, "Url", "url") ?? "";
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            // Always derive registrable domain from URL so subdomains roll up
            // (copilot.github.com → https://github.com), even if payload has Domain.
            var domain = BrowseDomain.FromUrl(url);
            if (string.IsNullOrWhiteSpace(domain))
            {
                continue;
            }

            yield return new ParsedVisit
            {
                Domain = domain,
                Url = url,
                Title = AgentEventPayload.String(v, "Title", "title") ?? "",
                Profile = AgentEventPayload.String(v, "Profile", "profile") ?? "",
                VisitedAt = AgentEventPayload.Date(v, "VisitedAt", "visitedAt") ?? DateTimeOffset.MinValue,
                VisitId = AgentEventPayload.Long(v, "VisitId", "visitId")
            };
        }
    }

    private static string NormalizeDomainKey(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return "";
        }

        // Accept either host or full https://host
        var d = domain.Trim();
        if (d.Contains("://", StringComparison.Ordinal))
        {
            return BrowseDomain.FromUrl(d);
        }

        return BrowseDomain.FromUrl("https://" + d.Trim().Trim('/'));
    }

    private sealed class ParsedVisit
    {
        public string Domain { get; init; } = "";
        public string Url { get; init; } = "";
        public string Title { get; init; } = "";
        public string Profile { get; init; } = "";
        public DateTimeOffset VisitedAt { get; init; }
        public long? VisitId { get; init; }
    }
}
