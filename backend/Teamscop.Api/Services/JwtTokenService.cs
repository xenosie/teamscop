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
    (string Token, long ExpiresInSeconds) CreateAccessToken(UserAccount user);
}

public sealed class JwtTokenService(IOptions<JwtOptions> options) : IJwtTokenService
{
    private readonly JwtOptions _options = options.Value;

    public (string Token, long ExpiresInSeconds) CreateAccessToken(UserAccount user)
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(_options.AccessTokenMinutes);
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
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: expires.UtcDateTime,
            signingCredentials: creds);

        var encoded = new JwtSecurityTokenHandler().WriteToken(token);
        var expiresIn = Math.Max(1, (long)(expires - DateTimeOffset.UtcNow).TotalSeconds);
        return (encoded, expiresIn);
    }
}
