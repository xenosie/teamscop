namespace Teamscop.Api.Options;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Key { get; set; } = string.Empty;
    public string Issuer { get; set; } = "teamscop";
    public string Audience { get; set; } = "teamscop-agent";

    /// <summary>
    /// Unused. Access tokens do not expire; kept only for backward-compatible config files.
    /// </summary>
    public int AccessTokenMinutes { get; set; } = 0;
}

public sealed class CompanyTokenOptions
{
    public const string SectionName = "CompanyToken";

    /// <summary>
    /// Base64-encoded 32-byte AES key shared with the Windows agent.
    /// </summary>
    public string Key { get; set; } = string.Empty;
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string AvatarRoot { get; set; } = "data/avatars";
    public string PublicAvatarBasePath { get; set; } = "/media/avatars";
}
