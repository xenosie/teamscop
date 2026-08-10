using System.Globalization;
using System.Text.Json;

namespace Teamscop.Api.Services.Insights;

/// <summary>
/// The one reader for the agent event envelope.
///
/// A stored payload is either the vault envelope <c>{ "payloadBase64": "..." }</c> or the inner
/// body inline (older rows and tests). Inside, property names may be camelCase or PascalCase
/// depending on which agent build wrote them, so every accessor probes a list of candidates.
/// </summary>
public static class AgentEventPayload
{
    /// <summary>
    /// Opens the inner body. The caller owns the returned document.
    ///
    /// <paramref name="inlineMarkers"/> names the properties that identify an already-unwrapped
    /// body; pass none to accept any inline JSON object.
    /// </summary>
    public static JsonDocument? TryOpen(string? payloadJson, params string[] inlineMarkers)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var outer = JsonDocument.Parse(payloadJson);
            var root = outer.RootElement;
            if (root.TryGetProperty("payloadBase64", out var wrapped)
                && wrapped.ValueKind == JsonValueKind.String)
            {
                var b64 = wrapped.GetString();
                if (string.IsNullOrWhiteSpace(b64))
                {
                    return null;
                }

                return JsonDocument.Parse(Convert.FromBase64String(b64));
            }

            if (inlineMarkers.Length == 0 || inlineMarkers.Any(m => root.TryGetProperty(m, out _)))
            {
                // Re-parse so the caller owns a document that outlives `outer`.
                return JsonDocument.Parse(payloadJson);
            }

            return null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    public static string? String(JsonElement el, params string[] names)
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

    public static int? Int(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (!el.TryGetProperty(name, out var p))
            {
                continue;
            }

            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n))
            {
                return n;
            }

            if (p.ValueKind == JsonValueKind.String
                && int.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    public static long? Long(JsonElement el, params string[] names)
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

    public static bool? Bool(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (!el.TryGetProperty(name, out var p))
            {
                continue;
            }

            if (p.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (p.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }

        return null;
    }

    public static DateTimeOffset? Date(JsonElement el, params string[] names)
        => Date(el, DateTimeStyles.RoundtripKind, names);

    /// <summary>
    /// <paramref name="styles"/> decides how an offset-less timestamp is read. Timetrack segments
    /// pass <c>AssumeUniversal | AdjustToUniversal</c> because the agent writes UTC wall clocks.
    /// </summary>
    public static DateTimeOffset? Date(JsonElement el, DateTimeStyles styles, params string[] names)
    {
        foreach (var name in names)
        {
            if (!el.TryGetProperty(name, out var p))
            {
                continue;
            }

            if (p.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(p.GetString(), CultureInfo.InvariantCulture, styles, out var parsed))
            {
                return parsed;
            }

            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var unix))
            {
                // Heuristic: seconds vs milliseconds.
                return unix > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unix)
                    : DateTimeOffset.FromUnixTimeSeconds(unix);
            }
        }

        return null;
    }
}
