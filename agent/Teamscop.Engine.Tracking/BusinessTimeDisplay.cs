using System.Globalization;

namespace Teamscop.Engine.Tracking;

/// <summary>
/// Format event times on the company business timeline (§8.2 — always company time,
/// never employee-local and never viewer-local).
/// Preference order: the server's projected business time, then the business time the
/// agent stamped into the payload, then a local conversion via the company timezone.
/// </summary>
public static class BusinessTimeDisplay
{
    public static string FormatEventTime(TrackingEventItem evt, BusinessClock? companyClock = null)
    {
        if (evt.BusinessOccurredAt is { } bizAt)
        {
            return bizAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        if (TryBusinessLocalFromPayload(evt.PayloadJson, out var fromPayload))
        {
            return fromPayload;
        }

        if (companyClock is not null)
        {
            return companyClock.At(evt.OccurredAt).BusinessLocal
                .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        // Last resort: UTC (not machine-local) so admins in different OSes stay consistent.
        return evt.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
    }

    public static string FormatUtcInstant(DateTimeOffset utc, BusinessClock companyClock)
        => companyClock.At(utc).BusinessLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static bool TryBusinessLocalFromPayload(string? payloadJson, out string label)
    {
        label = "";
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return false;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("businessLocal", out var el)
                && el.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var s = el.GetString();
                if (string.IsNullOrWhiteSpace(s))
                {
                    return false;
                }

                if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces, out var dt))
                {
                    label = dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    return true;
                }

                label = s;
                return true;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        return false;
    }
}
