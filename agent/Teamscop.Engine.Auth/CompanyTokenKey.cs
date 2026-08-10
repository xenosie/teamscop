namespace Teamscop.Engine.Auth;

/// <summary>
/// Shared AES-256 key for offline company tokens.
/// Must match server config CompanyToken__Key (base64).
/// Replace at build/release time for production.
/// </summary>
public static class CompanyTokenKey
{
    // Development default (matches appsettings.Development.json). Override in production builds.
    public const string Base64 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    public static bool IsAllZeroBase64(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return true;
        }

        try
        {
            var bytes = Convert.FromBase64String(key.Trim());
            return bytes.Length == 0 || bytes.All(b => b == 0);
        }
        catch
        {
            return false;
        }
    }
}
