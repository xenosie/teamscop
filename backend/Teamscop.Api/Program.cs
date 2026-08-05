using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.FileProviders;
using Teamscop.Api.Data;
using Teamscop.Api.Endpoints;
using Teamscop.Api.Hubs;
using Teamscop.Api.Options;
using Teamscop.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<CompanyTokenOptions>(builder.Configuration.GetSection(CompanyTokenOptions.SectionName));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("Jwt configuration missing.");
var companyToken = builder.Configuration.GetSection(CompanyTokenOptions.SectionName).Get<CompanyTokenOptions>()
    ?? throw new InvalidOperationException("CompanyToken configuration missing.");

if (string.IsNullOrWhiteSpace(jwt.Key) || jwt.Key.Length < 32)
{
    throw new InvalidOperationException("Jwt:Key must be at least 32 characters.");
}

if (string.IsNullOrWhiteSpace(companyToken.Key))
{
    throw new InvalidOperationException("CompanyToken:Key (base64 32-byte key) is required.");
}

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Connection string 'Default' is required.");

builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (connectionString.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
    {
        options.UseInMemoryDatabase("teamscop");
    }
    else
    {
        options.UseNpgsql(connectionString);
    }
});

builder.Services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IAvatarStorage, AvatarStorage>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ILifecycleService, LifecycleService>();
builder.Services.AddScoped<IIngestService, IngestService>();
builder.Services.AddScoped<ITrackingConfigService, TrackingConfigService>();
builder.Services.AddScoped<IBusinessTimeService, BusinessTimeService>();
builder.Services.AddScoped<IAuthorityService, AuthorityService>();
builder.Services.AddScoped<ITeamService, TeamService>();
builder.Services.AddScoped<ITrackingQueryService, TrackingQueryService>();
builder.Services.AddSignalR();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = "sub",
            RoleClaimType = ClaimTypes.Role
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseForwardedHeaders();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (!connectionString.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
    {
        db.Database.Migrate();
    }
    else
    {
        db.Database.EnsureCreated();
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

var storage = app.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
var avatarRoot = Path.IsPathRooted(storage.AvatarRoot)
    ? storage.AvatarRoot
    : Path.Combine(app.Environment.ContentRootPath, storage.AvatarRoot);
Directory.CreateDirectory(avatarRoot);

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(avatarRoot),
    RequestPath = storage.PublicAvatarBasePath
});

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "teamscop-api" }));
app.MapAuthEndpoints().RequireRateLimiting("auth");
app.MapLifecycleEndpoints().RequireRateLimiting("auth");
app.MapIngestEndpoints().RequireRateLimiting("auth");
app.MapTrackingEndpoints().RequireRateLimiting("auth");
app.MapBusinessTimeEndpoints().RequireRateLimiting("auth");
app.MapTeamEndpoints().RequireRateLimiting("auth");
app.MapPoliceEndpoints().RequireRateLimiting("auth");
app.MapHub<ConfigHub>("/hubs/config");

app.Run();

public partial class Program;
