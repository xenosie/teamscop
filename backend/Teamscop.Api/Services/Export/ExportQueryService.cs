using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;
using Teamscop.Api.Services.Insights;
using Teamscop.Engine.Sync;
using Teamscop.Engine.Tracking;

namespace Teamscop.Api.Services.Export;

public sealed record ExportStaffDto(
    Guid StaffUserId,
    string Username,
    string? Status,
    string? StatusReason,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset CreatedAt);

public sealed record ExportLeaderDto(Guid StaffUserId, string Username, Guid TeamId, string TeamName);

public sealed record ExportTeamMemberDto(Guid StaffUserId, string Username, DateTimeOffset JoinedAt);

public sealed record ExportTeamDto(Guid TeamId, string TeamName, ExportLeaderDto? Leader, IReadOnlyList<ExportTeamMemberDto> Members);

public sealed record ExportBusinessTimeDto(
    Guid CompanyId,
    string CompanyName,
    string TimeZoneId,
    string BusinessLocalNow,
    string UtcNow,
    double UtcOffsetHours);

public sealed record ExportScreenshotDisplayDto(int DisplayIndex, int Width, int Height, int Size, string ImageUrl);

public sealed record ExportScreenshotDto(
    Guid EventId,
    Guid StaffUserId,
    DateTimeOffset OccurredAt,
    string BusinessOccurredAt,
    IReadOnlyList<ExportScreenshotDisplayDto> Displays);

public sealed record ExportTimeTrackSegmentDto(
    string Kind,
    DateTimeOffset Start,
    DateTimeOffset End,
    double DurationSeconds);

public sealed record ExportTimeTrackDto(
    Guid StaffUserId,
    DateTimeOffset From,
    DateTimeOffset To,
    double WorkedSeconds,
    double IdleSeconds,
    IReadOnlyList<ExportTimeTrackSegmentDto> Segments);

public sealed record ExportVisitDto(
    string Url,
    string Domain,
    string Title,
    DateTimeOffset VisitedAt);

public sealed record ExportBrowsingDto(Guid StaffUserId, DateTimeOffset From, DateTimeOffset To, IReadOnlyList<ExportVisitDto> Visits);

public interface IExportQueryService
{
    Task<ExportBusinessTimeDto?> GetBusinessTimeAsync(Guid companyId, CancellationToken ct);
    Task<IReadOnlyList<ExportStaffDto>> ListStaffAsync(Guid companyId, CancellationToken ct);
    Task<IReadOnlyList<ExportLeaderDto>> ListLeadersAsync(Guid companyId, CancellationToken ct);
    Task<ExportTeamDto?> GetTeamAsync(Guid companyId, Guid teamId, CancellationToken ct);
    Task<IReadOnlyList<ExportScreenshotDto>> ListScreenshotsAsync(
        Guid companyId, Guid staffUserId, DateTimeOffset from, DateTimeOffset to, int take, CancellationToken ct);
    Task<ExportTimeTrackDto?> GetTimeTrackAsync(
        Guid companyId, Guid staffUserId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    Task<ExportBrowsingDto?> GetBrowsingAsync(
        Guid companyId, Guid staffUserId, DateTimeOffset from, DateTimeOffset to, int take, CancellationToken ct);
    Task<(byte[] Bytes, string ContentType)?> GetScreenshotImageAsync(
        Guid companyId, Guid eventId, int displayIndex, CancellationToken ct);
}

/// <summary>
/// Read-only queries for the export API.
///
/// Every method takes the CREDENTIAL's company id and filters on it, and every staff lookup is
/// verified to belong to that company before any data is read. The export path deliberately does
/// not reuse <c>IStaffDataGuard</c>: that guard answers "may this VIEWER see this staff member",
/// a question about JWT principals, packages and team membership that has no meaning for a machine
/// credential. Reusing it would have meant inventing a fake principal — and a fake principal is
/// exactly the kind of thing that later grows permissions nobody intended. The rule here is much
/// simpler and therefore much easier to keep true: one company, read-only, nothing else.
/// </summary>
public sealed class ExportQueryService(AppDbContext db, IScreenshotBlobStorage blobs) : IExportQueryService
{
    /// <summary>Hard ceiling on any period, so one request cannot ask for a year of screenshots.</summary>
    public static readonly TimeSpan MaxPeriod = TimeSpan.FromDays(31);

    public async Task<ExportBusinessTimeDto?> GetBusinessTimeAsync(Guid companyId, CancellationToken ct)
    {
        var company = await db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new { c.Id, c.Name, c.BusinessTimeZoneId })
            .FirstOrDefaultAsync(ct);
        if (company is null)
        {
            return null;
        }

        var zone = CompanyBusinessTime.Resolve(company.BusinessTimeZoneId);
        var utcNow = DateTimeOffset.UtcNow;
        var local = CompanyBusinessTime.ToBusinessLocal(utcNow, zone);
        return new ExportBusinessTimeDto(
            company.Id,
            company.Name,
            company.BusinessTimeZoneId,
            local.ToString("yyyy-MM-dd'T'HH:mm:ss"),
            utcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            zone.GetUtcOffset(utcNow).TotalHours);
    }

    public async Task<IReadOnlyList<ExportStaffDto>> ListStaffAsync(Guid companyId, CancellationToken ct)
        => await db.Users.AsNoTracking()
            .Where(u => u.CompanyId == companyId && u.Role == UserRole.Staff)
            .OrderBy(u => u.Username)
            .Select(u => new ExportStaffDto(
                u.Id, u.Username, u.AgentStatus, u.AgentStatusReason, u.LastHeartbeatAt, u.CreatedAt))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ExportLeaderDto>> ListLeadersAsync(Guid companyId, CancellationToken ct)
        => await db.Teams.AsNoTracking()
            .Where(t => t.CompanyId == companyId && t.LeaderUserId != null)
            .OrderBy(t => t.Name)
            .Select(t => new ExportLeaderDto(
                t.LeaderUserId!.Value, t.Leader!.Username, t.Id, t.Name))
            .ToListAsync(ct);

    public async Task<ExportTeamDto?> GetTeamAsync(Guid companyId, Guid teamId, CancellationToken ct)
    {
        var team = await db.Teams.AsNoTracking()
            .Where(t => t.CompanyId == companyId && t.Id == teamId)
            .Select(t => new
            {
                t.Id,
                t.Name,
                t.LeaderUserId,
                LeaderName = t.Leader!.Username,
                Members = t.Members
                    .Select(m => new ExportTeamMemberDto(m.StaffUserId, m.StaffUser.Username, m.JoinedAt))
                    .ToList()
            })
            .FirstOrDefaultAsync(ct);

        if (team is null)
        {
            return null;
        }

        var leader = team.LeaderUserId is { } leaderId
            ? new ExportLeaderDto(leaderId, team.LeaderName, team.Id, team.Name)
            : null;
        return new ExportTeamDto(team.Id, team.Name, leader, team.Members.OrderBy(m => m.Username).ToList());
    }

    public async Task<IReadOnlyList<ExportScreenshotDto>> ListScreenshotsAsync(
        Guid companyId, Guid staffUserId, DateTimeOffset from, DateTimeOffset to, int take, CancellationToken ct)
    {
        if (!await BelongsToCompanyAsync(companyId, staffUserId, ct))
        {
            return [];
        }

        take = Math.Clamp(take <= 0 ? 200 : take, 1, 500);
        var rows = await db.AgentEvents.AsNoTracking()
            .ForStaff(staffUserId)
            .OfType(AgentEventTypes.ScreenshotMeta)
            .InPeriod(from, to)
            .Newest(take)
            .Select(e => new { e.Id, e.UserId, e.OccurredAt, e.PayloadJson })
            .ToListAsync(ct);

        var zone = CompanyBusinessTime.Resolve(await TimeZoneIdAsync(companyId, ct));
        var list = new List<ExportScreenshotDto>(rows.Count);
        foreach (var row in rows)
        {
            var displays = ParseDisplays(row.PayloadJson, row.Id);
            list.Add(new ExportScreenshotDto(
                row.Id,
                row.UserId,
                row.OccurredAt,
                CompanyBusinessTime.ToBusinessLocal(row.OccurredAt, zone).ToString("yyyy-MM-dd'T'HH:mm:ss"),
                displays));
        }

        return list;
    }

    /// <summary>
    /// The image bytes for one display of one capture. Separated from the metadata listing on
    /// purpose: a period of screenshots is a list of URLs, and the consumer fetches only the frames
    /// it actually wants. Inlining base64 would have made a one-day export tens of megabytes of
    /// JSON, most of it never looked at.
    /// </summary>
    public async Task<(byte[] Bytes, string ContentType)?> GetScreenshotImageAsync(
        Guid companyId, Guid eventId, int displayIndex, CancellationToken ct)
    {
        displayIndex = displayIndex <= 0 ? 1 : displayIndex;

        // Company scope is enforced on the EVENT ROW, so an event id from another tenant cannot be
        // read even if the caller somehow learns it.
        var owner = await db.AgentEvents.AsNoTracking()
            .OfType(AgentEventTypes.ScreenshotMeta)
            .Where(e => e.Id == eventId && e.CompanyId == companyId)
            .Select(e => (Guid?)e.UserId)
            .FirstOrDefaultAsync(ct);
        if (owner is null)
        {
            return null;
        }

        var bytes = await blobs.ReadDisplayAsync(owner.Value, eventId, displayIndex, ct);
        return bytes is null ? null : (bytes, DetectMime(bytes));
    }

    public async Task<ExportTimeTrackDto?> GetTimeTrackAsync(
        Guid companyId, Guid staffUserId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (!await BelongsToCompanyAsync(companyId, staffUserId, ct))
        {
            return null;
        }

        // A small lead-in so a segment that started before the window but overlaps it is included —
        // the same rule the product's own timeline uses, so exported totals match the admin UI.
        var rows = await db.AgentEvents.AsNoTracking()
            .ForStaff(staffUserId)
            .OfType(AgentEventTypes.TimeTrack)
            .InPeriod(from.AddHours(-6), to)
            .Oldest()
            .Select(e => e.PayloadJson)
            .ToListAsync(ct);

        var segments = new List<ExportTimeTrackSegmentDto>();
        double worked = 0, idle = 0;
        foreach (var payload in rows)
        {
            if (!TimeTrackSegmentReader.TryRead(payload, out var seg))
            {
                continue;
            }

            var start = seg.Start < from ? from : seg.Start;
            var end = seg.End > to ? to : seg.End;
            if (end <= start)
            {
                continue;
            }

            var seconds = (end - start).TotalSeconds;
            if (seg.Working)
            {
                worked += seconds;
            }
            else
            {
                idle += seconds;
            }

            segments.Add(new ExportTimeTrackSegmentDto(seg.Kind, start, end, seconds));
        }

        return new ExportTimeTrackDto(staffUserId, from, to, worked, idle, segments);
    }

    public async Task<ExportBrowsingDto?> GetBrowsingAsync(
        Guid companyId, Guid staffUserId, DateTimeOffset from, DateTimeOffset to, int take, CancellationToken ct)
    {
        if (!await BelongsToCompanyAsync(companyId, staffUserId, ct))
        {
            return null;
        }

        take = Math.Clamp(take <= 0 ? 200 : take, 1, 500);
        var rows = await db.AgentEvents.AsNoTracking()
            .ForStaff(staffUserId)
            .OfType(AgentEventTypes.BrowserHistory)
            .InPeriod(from, to)
            .Newest(take)
            .Select(e => e.PayloadJson)
            .ToListAsync(ct);

        var visits = new List<ExportVisitDto>();
        foreach (var payload in rows)
        {
            visits.AddRange(ParseVisits(payload));
        }

        var deduped = visits
            .GroupBy(v => (v.Url, v.VisitedAt))
            .Select(g => g.First())
            .OrderByDescending(v => v.VisitedAt)
            .ToList();

        return new ExportBrowsingDto(staffUserId, from, to, deduped);
    }

    private async Task<bool> BelongsToCompanyAsync(Guid companyId, Guid staffUserId, CancellationToken ct)
        => await db.Users.AsNoTracking()
            .AnyAsync(u => u.Id == staffUserId && u.CompanyId == companyId && u.Role == UserRole.Staff, ct);

    private async Task<string> TimeZoneIdAsync(Guid companyId, CancellationToken ct)
        => await db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => c.BusinessTimeZoneId)
            .FirstOrDefaultAsync(ct) ?? "UTC";

    private static List<ExportScreenshotDisplayDto> ParseDisplays(string payloadJson, Guid eventId)
    {
        var result = new List<ExportScreenshotDisplayDto>();
        using var doc = AgentEventPayload.TryOpen(payloadJson, "displays");
        if (doc is null || !doc.RootElement.TryGetProperty("displays", out var displays)
            || displays.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return result;
        }

        foreach (var d in displays.EnumerateArray())
        {
            var index = ReadInt(d, "displayIndex", "DisplayIndex");
            index = index <= 0 ? 1 : index;
            result.Add(new ExportScreenshotDisplayDto(
                index,
                ReadInt(d, "width", "Width"),
                ReadInt(d, "height", "Height"),
                ReadInt(d, "size", "Size"),
                $"/api/v2/screenshots/{eventId:D}/image?display={index}"));
        }

        return result;
    }

    private static IEnumerable<ExportVisitDto> ParseVisits(string payloadJson)
    {
        using var doc = AgentEventPayload.TryOpen(payloadJson, "visits");
        if (doc is null || !doc.RootElement.TryGetProperty("visits", out var visits)
            || visits.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var v in visits.EnumerateArray())
        {
            var url = ReadString(v, "url", "Url") ?? "";
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var visitedAt = ReadDate(v, "visitedAt", "VisitedAt");
            yield return new ExportVisitDto(
                url,
                BrowseDomain.FromUrl(url),
                ReadString(v, "title", "Title") ?? "",
                visitedAt ?? DateTimeOffset.MinValue);
        }
    }

    private static int ReadInt(System.Text.Json.JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
                && v.TryGetInt32(out var i))
            {
                return i;
            }
        }

        return 0;
    }

    private static string? ReadString(System.Text.Json.JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return v.GetString();
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadDate(System.Text.Json.JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                && DateTimeOffset.TryParse(v.GetString(), out var d))
            {
                return d;
            }
        }

        return null;
    }

    private static string DetectMime(byte[] bytes)
    {
        if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I'
            && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            return "image/webp";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        return "image/webp";
    }
}
