using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Teamscop.Api.Audit;
using Teamscop.Api.Data;
using Teamscop.Api.Options;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Sync;
using Teamscop.Api.Errors;

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
    Task ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken ct);
}

public sealed class AuthService(
    AppDbContext db,
    IPasswordHasher passwordHasher,
    IJwtTokenService jwtTokenService,
    IAvatarStorage avatarStorage,
    IOptions<CompanyTokenOptions> companyTokenOptions,
    IAuditLog audit,
    ITeamService teams,
    ILogger<AuthService> logger) : IAuthService
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

        var normalizedKey = NormalizeDeviceKey(deviceKey);
        var existingAdmin = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.DeviceKey == normalizedKey, ct);
        if (existingAdmin is not null)
        {
            // Not re-adopted the way staff Join is: registering again would mint a SECOND company
            // and silently orphan the first. Sign-in is the correct path for an admin machine.
            throw new InvalidOperationException(
                $"This PC is already registered as '{existingAdmin.Username}' ({existingAdmin.Role}). " +
                (existingAdmin.Role == UserRole.Admin
                    ? "Sign in with your password instead — registering again would create a second business."
                    : "This is a staff PC. Use Join with the business token, or sign in with your password."));
        }

        var companyId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var tokenJti = Guid.NewGuid();

        var companyAvatar = await avatarStorage.SaveAsync(avatar, ct);
        var company = new Company
        {
            Id = companyId,
            Name = username.Trim(),
            AvatarUrl = companyAvatar,
            TokenJti = tokenJti,
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
        await SaveEnrollmentAsync(ct);

        audit.Record(AuditActions.CompanyCreated, admin.Id, company.Id, new { company.Name, admin.Username });
        logger.LogInformation("Company {CompanyId} created by admin {AdminUserId}", company.Id, admin.Id);

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

        var staffKey = NormalizeDeviceKey(deviceKey);
        var existingStaff = await db.Users
            .FirstOrDefaultAsync(u => u.DeviceKey == staffKey, ct);

        if (existingStaff is not null)
        {
            // Re-adoption. Identity is the hardware (§1.1), so a machine that is reinstalled comes
            // back as the SAME machine — the device key is unchanged by definition. Refusing here
            // made a reinstalled PC unenrollable: Join was rejected, and the old advice ("ask an
            // admin to remove the old device") named a capability the product does not have and
            // never will (§1.7). Holding the company token is the same proof of belonging that
            // authorised the first Join, so it authorises reclaiming the row.
            //
            // The account id is preserved deliberately: history, teams and TOTP enrolment stay
            // attached to the machine across a reinstall, which is the whole point of §1.3.
            if (existingStaff.Role != UserRole.Staff)
            {
                throw new InvalidOperationException(
                    $"This PC is registered as '{existingStaff.Username}' (Admin). " +
                    "An admin machine cannot be re-enrolled as staff. Sign in instead.");
            }

            if (existingStaff.CompanyId != company.Id)
            {
                throw new InvalidOperationException(
                    "This PC is already enrolled in a different business. " +
                    "Uninstall Teamscop on this machine before joining another business.");
            }

            var readoptedAvatar = await avatarStorage.SaveAsync(avatar, ct);
            existingStaff.Username = username.Trim();
            existingStaff.PasswordHash = passwordHasher.Hash(password);
            if (!string.IsNullOrWhiteSpace(readoptedAvatar))
            {
                existingStaff.AvatarUrl = readoptedAvatar;
            }

            db.AgentEvents.Add(BuildRegistrationEvent(company.Id, existingStaff, readopted: true));
            await SaveEnrollmentAsync(ct);

            audit.Record(AuditActions.StaffEnrolled, existingStaff.Id, company.Id,
                new { existingStaff.Username, readopted = true });
            logger.LogInformation(
                "Device re-adopted: staff {StaffUserId} re-enrolled on the same hardware", existingStaff.Id);

            await NotifyRosterSafeAsync(company.Id, ct);
            return CreateSession(existingStaff, company, companyToken: null);
        }

        var staffId = Guid.NewGuid();
        var staffAvatar = await avatarStorage.SaveAsync(avatar, ct);
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
        db.AgentEvents.Add(BuildRegistrationEvent(company.Id, staff, readopted: false));
        await SaveEnrollmentAsync(ct);

        audit.Record(AuditActions.StaffEnrolled, staff.Id, company.Id,
            new { staff.Username, deviceKeyPrefix = DevicePrefix(staff.DeviceKey) });
        logger.LogInformation("Staff {StaffUserId} enrolled into company {CompanyId}", staff.Id, company.Id);

        await NotifyRosterSafeAsync(company.Id, ct);
        return CreateSession(staff, company, companyToken: null);
    }

    private static string DevicePrefix(string deviceKey)
        => deviceKey.Length >= 8 ? deviceKey[..8] : deviceKey;

    /// <summary>
    /// The synthetic <c>registration</c> row that puts an enrolment into App history (§7.4).
    /// <paramref name="readopted"/> marks a reinstall reclaiming its own machine, so the admin can
    /// tell "a new PC appeared" from "the same PC came back".
    /// </summary>
    private static AgentEvent BuildRegistrationEvent(Guid companyId, UserAccount staff, bool readopted)
    {
        var occurred = DateTimeOffset.UtcNow;
        return new AgentEvent
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            UserId = staff.Id,
            ClientEventId = Guid.NewGuid(),
            EventType = AgentEventTypes.Registration,
            OccurredAt = occurred,
            ReceivedAt = occurred,
            PayloadJson = JsonSerializer.Serialize(new
            {
                kind = AgentEventTypes.Registration,
                username = staff.Username,
                deviceKeyPrefix = DevicePrefix(staff.DeviceKey),
                readopted
            })
        };
    }

    /// <summary>
    /// §2.3 — the machine reports from this moment, so the admin must SEE it from this moment.
    /// Without this push the desktop's staff directory only loaded at cold start, so an admin with
    /// the app already open watched an empty dropdown indefinitely.
    ///
    /// A broadcast failure must never fail an enrolment: the account exists and the agent is about
    /// to upload. A missed push costs a stale list until the next reload; a thrown one costs the
    /// enrolment itself.
    /// </summary>
    private async Task NotifyRosterSafeAsync(Guid companyId, CancellationToken ct)
    {
        try
        {
            await teams.NotifyRosterChangedAsync(companyId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Roster broadcast failed for company {CompanyId}", companyId);
        }
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
            ?? throw new SessionInvalidException("User not found.");

        if (user.Role != UserRole.Admin)
        {
            throw new UnauthorizedAccessException("Only admins can reveal the company token.");
        }

        return MintCompanyToken(user.Company, user.Id);
    }

    public async Task ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
        {
            throw new InvalidOperationException("Password must be at least 8 characters.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new SessionInvalidException("User not found.");

        if (!passwordHasher.Verify(currentPassword, user.PasswordHash))
        {
            throw new UnauthorizedAccessException("Current password is incorrect.");
        }

        user.PasswordHash = passwordHasher.Hash(newPassword);
        await db.SaveChangesAsync(ct);
        // §3.3 forbids forced logout, so outstanding tokens intentionally keep working.
        audit.Record(AuditActions.PasswordChanged, user.Id, user.CompanyId);
        logger.LogInformation("Password changed for user {UserId}", user.Id);
    }

    /// <summary>
    /// B7 — the pre-check narrows the window but the unique index on <c>users.DeviceKey</c> is the
    /// only real guard, and two installers racing on one machine used to surface as an unlogged
    /// 500. One machine holds one account by design (§1.3), so losing the race is an ordinary 400,
    /// not a server fault.
    /// </summary>
    private async Task SaveEnrollmentAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDeviceKeyConflict(ex))
        {
            logger.LogWarning(ex, "Concurrent signup lost the race for a device key");
            throw new InvalidOperationException(
                "This PC's device id was registered a moment ago by another signup. " +
                "Each PC can only hold one Teamscop account.");
        }
    }

    private static bool IsDeviceKeyConflict(DbUpdateException ex)
        => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
           && pg.ConstraintName?.Contains("DeviceKey", StringComparison.OrdinalIgnoreCase) == true;

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
