using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Services;

public sealed class StaffChainHealthDto
{
    public Guid StaffUserId { get; set; }
    public long LastVaultSequence { get; set; }
    public long GapCount { get; set; }
    public bool ChainBroken { get; set; }
    public long? BreakAfterSequence { get; set; }
    public long? ExpectedNextSequence { get; set; }
    public long? HighestSequenceFound { get; set; }
    public DateTimeOffset? LastBreakAt { get; set; }
    public string? BreakDetail { get; set; }
    public bool? HelperAlive { get; set; }
    public bool? TrackingOk { get; set; }
    public int? PendingOutbox { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public bool? Online { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public bool AgentOffline { get; set; }
    public string? BannerMessage { get; set; }
}

public interface IChainHealthService
{
    Task<StaffChainHealthDto> GetAsync(Guid viewerId, Guid staffUserId, CancellationToken ct);
}

public sealed class ChainHealthService(
    AppDbContext db,
    IAuthorityService authorities) : IChainHealthService
{
    private static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(2);

    public async Task<StaffChainHealthDto> GetAsync(Guid viewerId, Guid staffUserId, CancellationToken ct)
    {
        if (!await authorities.CanViewStaffAsync(viewerId, staffUserId, ct))
        {
            throw new UnauthorizedAccessException("Not allowed to view this staff member's tracking data.");
        }

        // Any monitoring package (or admin) may see chain health for staff they can view.
        var canMonitor = await authorities.CanViewEventTypeAsync(viewerId, AgentEventTypes.TimeTrack, ct)
                         || await authorities.CanViewEventTypeAsync(viewerId, AgentEventTypes.ScreenshotMeta, ct)
                         || await authorities.CanViewEventTypeAsync(viewerId, AgentEventTypes.BrowserHistory, ct)
                         || (await authorities.GetEffectiveAsync(viewerId, ct)).IsAdmin;
        if (!canMonitor)
        {
            throw new UnauthorizedAccessException("Missing authority package for tracking health.");
        }

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == staffUserId, ct)
                   ?? throw new InvalidOperationException("Staff not found.");
        var seq = await db.AgentSequenceStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == staffUserId, ct);

        var dto = new StaffChainHealthDto
        {
            StaffUserId = staffUserId,
            LastVaultSequence = seq?.LastVaultSequence ?? 0,
            GapCount = seq?.GapCount ?? 0,
            LastHeartbeatAt = user.LastHeartbeatAt,
            Online = user.LastOnline,
            LastSeenAt = user.LastSeenAt
        };

        dto.AgentOffline = user.LastHeartbeatAt is null
                           || DateTimeOffset.UtcNow - user.LastHeartbeatAt > OfflineAfter;

        await ApplyLatestVaultAlertAsync(dto, staffUserId, ct);
        await ApplyLatestHeartbeatHealthAsync(dto, staffUserId, ct);

        dto.ChainBroken = dto.GapCount > 0
                          || dto.BreakAfterSequence is not null
                          || dto.ExpectedNextSequence is not null;

        dto.BannerMessage = BuildBanner(dto);
        return dto;
    }

    private async Task ApplyLatestVaultAlertAsync(StaffChainHealthDto dto, Guid staffUserId, CancellationToken ct)
    {
        var row = await db.AgentEvents.AsNoTracking()
            .Where(e => e.UserId == staffUserId && e.EventType == AgentEventTypes.VaultAlert)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => new { e.OccurredAt, e.PayloadJson })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapPayload(row.PayloadJson));
            var root = doc.RootElement;
            var chainBreak = GetBool(root, "chainBreak", "ChainBreak") == true;
            var tampered = GetBool(root, "tamperedRecord", "TamperedRecord") == true;
            if (!chainBreak && !tampered && GetBool(root, "ok", "Ok") == true)
            {
                return;
            }

            dto.LastBreakAt = row.OccurredAt;
            dto.ExpectedNextSequence = GetLong(root, "expectedNextSequence", "ExpectedNextSequence");
            dto.HighestSequenceFound = GetLong(root, "highestSequenceFound", "HighestSequenceFound");
            if (dto.HighestSequenceFound is { } high)
            {
                dto.BreakAfterSequence = high;
            }
            else if (dto.ExpectedNextSequence is { } exp && exp > 0)
            {
                dto.BreakAfterSequence = exp - 1;
            }

            dto.BreakDetail = GetString(root, "error", "Error");
        }
        catch (JsonException)
        {
            dto.LastBreakAt = row.OccurredAt;
            dto.BreakDetail = "vault_alert present (unparseable payload)";
        }
    }

    private async Task ApplyLatestHeartbeatHealthAsync(StaffChainHealthDto dto, Guid staffUserId, CancellationToken ct)
    {
        var row = await db.AgentEvents.AsNoTracking()
            .Where(e => e.UserId == staffUserId && e.EventType == AgentEventTypes.Heartbeat)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => e.PayloadJson)
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapPayload(row));
            var root = doc.RootElement;
            dto.HelperAlive = GetBool(root, "helperAlive", "HelperAlive");
            dto.TrackingOk = GetBool(root, "trackingOk", "TrackingOk");
            var pending = GetLong(root, "pending", "Pending", "pendingOutbox", "PendingOutbox");
            if (pending is { } p)
            {
                dto.PendingOutbox = (int)Math.Clamp(p, 0, int.MaxValue);
            }
        }
        catch (JsonException)
        {
            // ignore
        }
    }

    private static string? BuildBanner(StaffChainHealthDto dto)
    {
        if (dto.AgentOffline)
        {
            return "Agent offline (no recent heartbeat).";
        }

        if (dto.HelperAlive == false)
        {
            return "Tracking helper not running.";
        }

        if (dto.TrackingOk == false)
        {
            return "Tracking tasks not healthy.";
        }

        if (dto.BreakAfterSequence is { } after)
        {
            return $"Data chain break after sequence {after}.";
        }

        if (dto.GapCount > 0)
        {
            return $"Data chain gaps detected (gap count {dto.GapCount}; last sequence {dto.LastVaultSequence}).";
        }

        if (!string.IsNullOrWhiteSpace(dto.BreakDetail))
        {
            return $"Data chain issue: {dto.BreakDetail}";
        }

        return null;
    }

    private static string UnwrapPayload(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("payloadBase64", out var b64)
                && b64.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(b64.GetString()))
            {
                return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64.GetString()!));
            }

            return payloadJson;
        }
        catch
        {
            return payloadJson;
        }
    }

    private static bool? GetBool(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var el))
            {
                if (el.ValueKind == JsonValueKind.True) return true;
                if (el.ValueKind == JsonValueKind.False) return false;
            }
        }

        return null;
    }

    private static long? GetLong(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var el) && el.TryGetInt64(out var n))
            {
                return n;
            }
        }

        return null;
    }

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            {
                return el.GetString();
            }
        }

        return null;
    }
}
