using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Teamscop.Api.Data;
using Teamscop.Api.Options;

namespace Teamscop.Api.Services;

public interface IJwtTokenService
{
    /// <summary>
    /// Creates a non-expiring access token. <paramref name="ExpiresInSeconds"/> is always 0
    /// (no lifetime / never expires).
    /// </summary>
    (string Token, long ExpiresInSeconds) CreateAccessToken(UserAccount user);
}

public sealed class JwtTokenService(IOptions<JwtOptions> options) : IJwtTokenService
{
    private readonly JwtOptions _options = options.Value;

    public (string Token, long ExpiresInSeconds) CreateAccessToken(UserAccount user)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new("device_key", user.DeviceKey),
            new("username", user.Username),
            new(ClaimTypes.Role, user.Role.ToString().ToLowerInvariant()),
            new("company_id", user.CompanyId.ToString())
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        // No `exp` / `nbf` — sessions do not expire. API validates signature + issuer/audience only.
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: null,
            expires: null,
            signingCredentials: creds);

        var encoded = new JwtSecurityTokenHandler().WriteToken(token);
        return (encoded, ExpiresInSeconds: 0);
    }
}
