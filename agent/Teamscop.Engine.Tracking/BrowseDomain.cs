namespace Teamscop.Engine.Tracking;

/// <summary>
/// Normalize visit URLs to a registrable site key for admin rollups.
/// Subdomains collapse: copilot.github.com → https://github.com
/// </summary>
public static class BrowseDomain
{
    /// <summary>
    /// Multi-part public suffixes where the registrable domain needs 3+ labels.
    /// </summary>
    private static readonly HashSet<string> MultiPartPublicSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk", "org.uk", "ac.uk", "gov.uk", "me.uk",
        "com.au", "net.au", "org.au", "edu.au",
        "co.jp", "ne.jp", "or.jp", "go.jp", "ac.jp",
        "com.br", "com.mx", "com.ar", "com.tr",
        "co.nz", "co.za", "co.in", "com.sg",
        "com.cn", "com.hk", "com.tw", "com.my",
        "github.io", "blogspot.com", "azurewebsites.net"
    };

    public static string FromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "";
        }

        var raw = url.Trim();
        if (!raw.Contains("://", StringComparison.Ordinal))
        {
            raw = "https://" + raw;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return "";
        }

        var host = RegistrableHost(uri.Host);
        if (string.IsNullOrWhiteSpace(host))
        {
            return "";
        }

        // Aggregate under https:// for stable keys across http/https.
        return "https://" + host;
    }

    public static string RegistrableHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return "";
        }

        var h = host.Trim().Trim('.').ToLowerInvariant();
        if (h.StartsWith("www.", StringComparison.Ordinal))
        {
            h = h[4..];
        }

        var parts = h.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "";
        }

        if (parts.Length == 1)
        {
            return parts[0];
        }

        // Multi-part suffix: foo.github.io → foo.github.io; a.b.co.uk → b.co.uk
        for (var suffixLen = Math.Min(3, parts.Length - 1); suffixLen >= 2; suffixLen--)
        {
            var suffix = string.Join('.', parts[(parts.Length - suffixLen)..]);
            if (!MultiPartPublicSuffixes.Contains(suffix))
            {
                continue;
            }

            var registrableLen = suffixLen + 1;
            if (parts.Length < registrableLen)
            {
                return h;
            }

            return string.Join('.', parts[(parts.Length - registrableLen)..]);
        }

        // Default eTLD+1: a.b.example.com → example.com
        return parts[^2] + "." + parts[^1];
    }
}
