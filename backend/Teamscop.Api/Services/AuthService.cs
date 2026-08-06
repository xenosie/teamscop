using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Teamscop.Api.Data;
using Teamscop.Api.Options;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Services;

public sealed record AuthUserDto(
    Guid Id,
    string DeviceKey,
    string Username,
    string Role,
    string? AvatarUrl,
    AuthCompanyDto Company);

public sealed record AuthCompanyDto(Guid Id, string Name, string? AvatarUrl);

public sealed record AuthSessionDto(
    string AccessToken,
    string TokenType,
    long ExpiresIn,
    string? CompanyToken,
    AuthUserDto User);

public interface IAuthService
{
    Task<AuthSessionDto> AdminSignupAsync(string deviceKey, string username, string password, IFormFile? avatar, CancellationToken ct);
    Task<AuthSessionDto> StaffSignupAsync(string deviceKey, string username, string password, string companyToken, IFormFile? avatar, CancellationToken ct);
    Task<AuthSessionDto> LoginAsync(string deviceKey, string password, CancellationToken ct);
    Task<AuthUserDto?> GetMeAsync(Guid userId, CancellationToken ct);
    Task<string> RevealCompanyTokenAsync(Guid userId, CancellationToken ct);
}

public sealed class AuthService(
    AppDbContext db,
    IPasswordHasher passwordHasher,
    IJwtTokenService jwtTokenService,
    IAvatarStorage avatarStorage,
    IOptions<CompanyTokenOptions> companyTokenOptions) : IAuthService
{
    private readonly CompanyTokenCodec _tokenCodec = CompanyTokenCodec.FromBase64Key(companyTokenOptions.Value.Key);

    public async Task<AuthSessionDto> AdminSignupAsync(
        string deviceKey,
        string username,
        string password,
        IFormFile? avatar,
        CancellationToken ct)
    {
        ValidateSignupInputs(deviceKey, username, password);

        if (await db.Users.AnyAsync(u => u.DeviceKey == deviceKey, ct))
        {
            throw new InvalidOperationException("Device is already registered.");
        }

        var companyId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var tokenJti = Guid.NewGuid();

        var companyAvatar = await avatarStorage.SaveAsync(companyId, avatar, ct);
        var company = new Company
        {
            Id = companyId,
            Name = username.Trim(),
            AvatarUrl = companyAvatar,
            TokenJti = tokenJti,
            TokenVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var admin = new UserAccount
        {
            Id = adminId,
            CompanyId = companyId,
            DeviceKey = NormalizeDeviceKey(deviceKey),
            Username = username.Trim(),
            PasswordHash = passwordHasher.Hash(password),
            Role = UserRole.Admin,
            AvatarUrl = companyAvatar,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Companies.Add(company);
        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);

        var companyToken = MintCompanyToken(company, admin.Id);
        return CreateSession(admin, company, companyToken);
    }

    public async Task<AuthSessionDto> StaffSignupAsync(
        string deviceKey,
        string username,
        string password,
        string companyToken,
        IFormFile? avatar,
        CancellationToken ct)
    {
        ValidateSignupInputs(deviceKey, username, password);
        if (string.IsNullOrWhiteSpace(companyToken))
        {
            throw new InvalidOperationException("Company token is required for staff signup.");
        }

        if (!_tokenCodec.TryDecrypt(companyToken, out var payload) || payload is null)
        {
            throw new InvalidOperationException("Invalid company token.");
        }

        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == payload.CompanyId, ct)
            ?? throw new InvalidOperationException("Company not found for token.");

        if (company.TokenJti != payload.Jti)
        {
            throw new InvalidOperationException("Company token has been revoked or rotated.");
        }

        if (await db.Users.AnyAsync(u => u.DeviceKey == NormalizeDeviceKey(deviceKey), ct))
        {
            throw new InvalidOperationException("Device is already registered.");
        }

        var staffId = Guid.NewGuid();
        var staffAvatar = await avatarStorage.SaveAsync(staffId, avatar, ct);
        var staff = new UserAccount
        {
            Id = staffId,
            CompanyId = company.Id,
            DeviceKey = NormalizeDeviceKey(deviceKey),
            Username = username.Trim(),
            PasswordHash = passwordHasher.Hash(password),
            Role = UserRole.Staff,
            AvatarUrl = staffAvatar,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Users.Add(staff);
        var devicePrefix = staff.DeviceKey.Length >= 8 ? staff.DeviceKey[..8] : staff.DeviceKey;
        var occurred = DateTimeOffset.UtcNow;
        var biz = CompanyBusinessTime.At(company, occurred);
        var registration = new AgentEvent
        {
            Id = Guid.NewGuid(),
            CompanyId = company.Id,
            UserId = staff.Id,
            ClientEventId = Guid.NewGuid(),
            EventType = AgentEventTypes.Registration,
            OccurredAt = occurred,
            ReceivedAt = occurred,
            PayloadJson = JsonSerializer.Serialize(new
            {
                kind = AgentEventTypes.Registration,
                username = staff.Username,
                deviceKeyPrefix = devicePrefix,
                businessLocal = biz.BusinessLocalIso,
                businessTimeZoneId = biz.TimeZoneId,
                businessClockVersion = biz.ClockVersion,
                businessSynchronized = biz.Synchronized
            })
        };
        CompanyBusinessTime.StampEvent(registration, company, occurred);
        db.AgentEvents.Add(registration);
        await db.SaveChangesAsync(ct);
        return CreateSession(staff, company, companyToken: null);
    }

    public async Task<AuthSessionDto> LoginAsync(string deviceKey, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceKey) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Device key and password are required.");
        }

        var normalized = NormalizeDeviceKey(deviceKey);
        var user = await db.Users.Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.DeviceKey == normalized, ct);

        if (user is null || !passwordHasher.Verify(password, user.PasswordHash))
        {
            throw new UnauthorizedAccessException("Invalid device key or password.");
        }

        string? companyToken = null;
        if (user.Role == UserRole.Admin)
        {
            companyToken = MintCompanyToken(user.Company, user.Id);
        }

        return CreateSession(user, user.Company, companyToken);
    }

    public async Task<AuthUserDto?> GetMeAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
        return user is null ? null : ToUserDto(user, user.Company);
    }

    public async Task<string> RevealCompanyTokenAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedAccessException("User not found.");

        if (user.Role != UserRole.Admin)
        {
            throw new UnauthorizedAccessException("Only admins can reveal the company token.");
        }

        return MintCompanyToken(user.Company, user.Id);
    }

    private string MintCompanyToken(Company company, Guid adminUserId)
    {
        var payload = new CompanyTokenPayload
        {
            Version = 1,
            CompanyId = company.Id,
            CompanyName = company.Name,
            AdminUserId = adminUserId,
            IssuedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Jti = company.TokenJti
        };
        return _tokenCodec.Encrypt(payload);
    }

    private AuthSessionDto CreateSession(UserAccount user, Company company, string? companyToken)
    {
        var (token, expiresIn) = jwtTokenService.CreateAccessToken(user);
        return new AuthSessionDto(
            token,
            "Bearer",
            expiresIn,
            companyToken,
            ToUserDto(user, company));
    }

    private static AuthUserDto ToUserDto(UserAccount user, Company company)
        => new(
            user.Id,
            user.DeviceKey,
            user.Username,
            user.Role.ToString().ToLowerInvariant(),
            user.AvatarUrl,
            new AuthCompanyDto(company.Id, company.Name, company.AvatarUrl));

    private static void ValidateSignupInputs(string deviceKey, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(deviceKey) || deviceKey.Trim().Length < 16)
        {
            throw new InvalidOperationException("Valid device key is required.");
        }

        if (string.IsNullOrWhiteSpace(username) || username.Trim().Length < 2)
        {
            throw new InvalidOperationException("Username must be at least 2 characters.");
        }

        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            throw new InvalidOperationException("Password must be at least 8 characters.");
        }
    }

    private static string NormalizeDeviceKey(string deviceKey)
        => deviceKey.Trim().ToLowerInvariant();
}
