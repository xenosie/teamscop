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
}
