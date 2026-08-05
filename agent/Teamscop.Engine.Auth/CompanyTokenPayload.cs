using System.Text.Json.Serialization;

namespace Teamscop.Engine.Auth;

public sealed class CompanyTokenPayload
{
    [JsonPropertyName("v")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("companyId")]
    public Guid CompanyId { get; set; }

    [JsonPropertyName("companyName")]
    public string CompanyName { get; set; } = string.Empty;

    [JsonPropertyName("adminUserId")]
    public Guid AdminUserId { get; set; }

    [JsonPropertyName("iat")]
    public long IssuedAtUnix { get; set; }

    [JsonPropertyName("jti")]
    public Guid Jti { get; set; }
}
