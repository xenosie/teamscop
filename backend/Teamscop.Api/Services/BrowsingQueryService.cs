using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;
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
    IAuthorityService authorities) : IBrowsingQueryService
{
    public async Task<IReadOnlyList<BrowsingDomainSummaryDto>> ListDomainsAsync(
        Guid viewerId,
        Guid staffUserId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int takeEvents,
        CancellationToken ct)
    {
        await EnsureCanViewAsync(viewerId, staffUserId, ct);
        var visits = await LoadVisitsAsync(staffUserId, from, to, takeEvents, ct);
        var groups = visits
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
        return groups;
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
        await EnsureCanViewAsync(viewerId, staffUserId, ct);
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
        await EnsureCanViewAsync(viewerId, staffUserId, ct);
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
                    Title = latest.Title ?? "",
                    VisitCount = g.Count(),
                    LastVisitedAt = g.Max(x => x.VisitedAt)
                };
            })
            .OrderByDescending(x => x.VisitCount)
            .ThenByDescending(x => x.LastVisitedAt)
            .Take(takeUrls)
            .ToList();
    }

    private async Task EnsureCanViewAsync(Guid viewerId, Guid staffUserId, CancellationToken ct)
    {
        if (!await authorities.CanViewStaffAsync(viewerId, staffUserId, ct))
        {
            throw new UnauthorizedAccessException("Not allowed to view this staff member's tracking data.");
        }

        if (!await authorities.CanViewEventTypeAsync(viewerId, AgentEventTypes.BrowserHistory, ct))
        {
            throw new UnauthorizedAccessException("Missing authority package for browsing history.");
        }
    }

    private async Task<List<ParsedVisit>> LoadVisitsAsync(
        Guid staffUserId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int takeEvents,
        CancellationToken ct)
    {
        takeEvents = Math.Clamp(takeEvents <= 0 ? 200 : takeEvents, 1, 500);
        var q = db.AgentEvents.AsNoTracking()
            .Where(e => e.UserId == staffUserId && e.EventType == AgentEventTypes.BrowserHistory);
        if (from is not null)
        {
            q = q.Where(e => e.OccurredAt >= from);
        }

        if (to is not null)
        {
            q = q.Where(e => e.OccurredAt < to);
        }

        var rows = await q.OrderByDescending(e => e.OccurredAt)
            .Take(takeEvents)
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
        using var innerDoc = OpenInnerDocument(payloadJson);
        if (innerDoc is null)
        {
            yield break;
        }

        var root = innerDoc.RootElement;
        if (!root.TryGetProperty("visits", out var visits)
            || visits.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var v in visits.EnumerateArray())
        {
            var url = ReadString(v, "Url", "url") ?? "";
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
                Title = ReadString(v, "Title", "title") ?? "",
                Profile = ReadString(v, "Profile", "profile") ?? "",
                VisitedAt = ReadDate(v, "VisitedAt", "visitedAt") ?? DateTimeOffset.MinValue,
                VisitId = ReadLong(v, "VisitId", "visitId")
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

    private static JsonDocument? OpenInnerDocument(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        using var outer = JsonDocument.Parse(payloadJson);
        var root = outer.RootElement;
        if (root.TryGetProperty("payloadBase64", out var pb)
            && pb.ValueKind == JsonValueKind.String)
        {
            var b64 = pb.GetString();
            if (string.IsNullOrWhiteSpace(b64))
            {
                return null;
            }

            try
            {
                return JsonDocument.Parse(Convert.FromBase64String(b64));
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                return null;
            }
        }

        if (root.TryGetProperty("visits", out _))
        {
            return JsonDocument.Parse(payloadJson);
        }

        return null;
    }

    private static string? ReadString(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
            {
                return p.GetString();
            }
        }

        return null;
    }

    private static long? ReadLong(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (!el.TryGetProperty(name, out var p))
            {
                continue;
            }

            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n))
            {
                return n;
            }

            if (p.ValueKind == JsonValueKind.String
                && long.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadDate(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (!el.TryGetProperty(name, out var p))
            {
                continue;
            }

            if (p.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(p.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var dto))
            {
                return dto;
            }

            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var unix))
            {
                // Heuristic: seconds vs ms
                return unix > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unix)
                    : DateTimeOffset.FromUnixTimeSeconds(unix);
            }
        }

        return null;
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
