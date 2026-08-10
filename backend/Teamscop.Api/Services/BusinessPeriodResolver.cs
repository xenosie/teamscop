using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;
using Teamscop.Api.Errors;

namespace Teamscop.Api.Services;

/// <summary>A period on the wire, resolved to a half-open [From, To) UTC range (null = open edge).</summary>
public readonly record struct ResolvedPeriod(DateTimeOffset? From, DateTimeOffset? To);

/// <summary>
/// The single place a calendar selection becomes UTC bounds (§2.2, §2.3). The admin's calendar
/// sends company-local days (<c>yyyy-MM-dd</c>); this converts each day through the caller's company
/// clock — the only frame of reference in the product (§2.1) — so calendar → period bounds →
/// timetrack bar span → displayed timestamps form one coherent chain, never a per-screen guess.
///
/// A full timestamp is already an absolute instant and passes through unchanged, which keeps
/// server-issued cursors and any absolute-instant caller working without re-interpreting them. The
/// company clock is read only when a bare DAY actually needs converting, so an instant or open-ended
/// query pays nothing. One timezone per company (§8.3), so the caller's clock is authoritative for
/// the whole range — the caller and every staff member they may query share it.
/// </summary>
public interface IBusinessPeriodResolver
{
    Task<ResolvedPeriod> ResolveAsync(Guid callerId, string? from, string? to, CancellationToken ct);
}

public sealed class BusinessPeriodResolver(AppDbContext db) : IBusinessPeriodResolver
{
    public async Task<ResolvedPeriod> ResolveAsync(Guid callerId, string? from, string? to, CancellationToken ct)
    {
        var fromIsDay = CompanyBusinessTime.TryParseCompanyDay(from, out var fromDay);
        var toIsDay = CompanyBusinessTime.TryParseCompanyDay(to, out var toDay);

        TimeZoneInfo? zone = null;
        if (fromIsDay || toIsDay)
        {
            var row = await db.Users.AsNoTracking()
                .Where(u => u.Id == callerId)
                .Select(u => new { u.Company.BusinessTimeZoneId })
                .FirstOrDefaultAsync(ct)
                ?? throw new SessionInvalidException("User not found.");
            zone = CompanyBusinessTime.Resolve(row.BusinessTimeZoneId);
        }

        return new ResolvedPeriod(
            ResolveEdge(from, fromIsDay, fromDay, zone, upper: false),
            ResolveEdge(to, toIsDay, toDay, zone, upper: true));
    }

    /// <summary>
    /// One edge of the range. A company-local day maps to its 00:00 on the lower edge and to the
    /// NEXT midnight on the upper edge, so an inclusive day selection [D..E] becomes the half-open
    /// instant range [D 00:00, E+1 00:00) — no off-by-one at midnight, and the day is 23h/25h across
    /// a DST spring-forward/fall-back because <see cref="CompanyBusinessTime.DayBounds"/> guards it.
    /// </summary>
    private static DateTimeOffset? ResolveEdge(string? value, bool isDay, DateOnly day, TimeZoneInfo? zone, bool upper)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (isDay && zone is not null)
        {
            var bounds = CompanyBusinessTime.DayBounds(day, zone);
            return upper ? bounds.To : bounds.From;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var instant))
        {
            return instant.ToUniversalTime();
        }

        // Unparseable — treat as absent rather than 400, matching the previous optional-bound
        // behaviour. Endpoints that REQUIRE both edges (leaderboard, timetrack) reject a null.
        return null;
    }
}
