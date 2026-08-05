namespace Teamscop.Engine.Auth;

public sealed class AuthSession
{
    public required string AccessToken { get; init; }
    public required string TokenType { get; init; }
    public required long ExpiresIn { get; init; }
    public string? CompanyToken { get; init; }
    public required AuthUser User { get; init; }
}

public sealed class AuthUser
{
    public required Guid Id { get; init; }
    public required string DeviceKey { get; init; }
    public required string Username { get; init; }
    public required string Role { get; init; }
    public string? AvatarUrl { get; init; }
    public required AuthCompany Company { get; init; }
}

public sealed class AuthCompany
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? AvatarUrl { get; init; }
}

public sealed class LoginRequest
{
    public required string DeviceKey { get; init; }
    public required string Password { get; init; }
}
