using System.Net;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SixLabors.ImageSharp.Memory;
using Teamscop.Api.Audit;
using Teamscop.Api.Data;
using Teamscop.Api.Endpoints;
using Teamscop.Api.Errors;
using Teamscop.Api.Hubs;
using Teamscop.Api.Options;
using Teamscop.Api.Services;
using Teamscop.Api.Services.Access;
using Teamscop.Api.Services.Insights;
using Teamscop.Api.Services.Export;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    // nginx terminates TLS on loopback — trust only that hop for client IP / rate limits.
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
});

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<CompanyTokenOptions>(builder.Configuration.GetSection(CompanyTokenOptions.SectionName));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection(RetentionOptions.SectionName));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.Configure<IngestOptions>(builder.Configuration.GetSection(IngestOptions.SectionName));

var ingestLimits = builder.Configuration.GetSection(IngestOptions.SectionName).Get<IngestOptions>()
    ?? new IngestOptions();
builder.WebHost.ConfigureKestrel(kestrel =>
{
    // Deliberate rather than the framework default, and above IngestOptions.MaxBatchBytes so the
    // aggregate cap answers 400 instead of the connection dying mid-upload (B10).
    kestrel.Limits.MaxRequestBodySize = ingestLimits.MaxRequestBodyBytes;
});

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

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddSingleton<ITotpSourceBackoff, TotpSourceBackoff>();
builder.Services.AddSingleton<IScreenshotBlobStorage, ScreenshotBlobStorage>();
builder.Services.AddScoped<IAuditLog, AuditLog>();
builder.Services.AddScoped<IAvatarStorage, AvatarStorage>();
// Singleton: it exists to hold the avatar authorization cache across requests (B12).
builder.Services.AddSingleton<IAvatarAccess, AvatarAccess>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ILifecycleService, LifecycleService>();
builder.Services.AddScoped<IIngestService, IngestService>();
builder.Services.AddScoped<ITrackingConfigService, TrackingConfigService>();
builder.Services.AddScoped<IBusinessTimeService, BusinessTimeService>();
// The single place a calendar selection becomes UTC bounds (§2.3).
builder.Services.AddScoped<IBusinessPeriodResolver, BusinessPeriodResolver>();
// Scoped, never singleton: a grant change must take effect on the next request (§4.3).
builder.Services.AddScoped<IAccessPolicy, AccessPolicy>();
builder.Services.AddScoped<IStaffDataGuard, StaffDataGuard>();
builder.Services.AddScoped<IPolicemanAdminService, PolicemanAdminService>();
builder.Services.AddScoped<ITeamService, TeamService>();
builder.Services.AddScoped<ITrackingQueryService, TrackingQueryService>();
builder.Services.AddScoped<IScreenshotMediaService, ScreenshotMediaService>();
builder.Services.AddScoped<IBrowsingQueryService, BrowsingQueryService>();
builder.Services.AddScoped<ITimeTrackQueryService, TimeTrackQueryService>();
builder.Services.AddScoped<IWorkSummaryService, WorkSummaryService>();
builder.Services.AddScoped<IStaffPresenceService, StaffPresenceService>();
builder.Services.AddScoped<IAgentHealthService, AgentHealthService>();
builder.Services.AddScoped<IAvatarUrlRepair, AvatarUrlRepair>();
builder.Services.AddHostedService<RetentionHostedService>();
// §14 — classifies every staff machine on its own clock, so a machine that breaks overnight is
// recorded when it breaks rather than when an admin next opens the roster.
builder.Services.AddHostedService<StaffStatusHostedService>();
// Runs once at start-up so a stale avatar prefix heals itself; no operator SQL (B12).
builder.Services.AddHostedService<AvatarUrlRepairHostedService>();
builder.Services.AddSignalR();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            // Access tokens are intentionally non-expiring (no fixed session lifetime).
            ValidateLifetime = false,
            RequireExpirationTime = false,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            NameClaimType = "sub",
            RoleClaimType = ClaimTypes.Role
        };

        // SignalR WebSockets send the JWT as ?access_token= (not Authorization header).
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken)
                    && path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Concurrency around Argon2id hashing (M12) plus modest IP window via low permit queue.
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetConcurrencyLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = 4,
                QueueLimit = 16,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));
    options.AddPolicy("lifecycleAnon", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    // The export API is one machine consumer, so it is limited per KEY rather than per IP: a
    // shared office NAT must not let ordinary product traffic consume the export budget, and a
    // stolen key must not get more throughput by calling from many addresses.
    options.AddPolicy("exportApi", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Request.Headers.TryGetValue("X-Api-Key", out var apiKey)
                ? apiKey.ToString()
                : httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    // Heartbeat + ingest + viewers from office NAT need headroom.
    options.AddPolicy("api", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 600,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Agents gzip their ingest batches — base64-in-JSON screenshots deflate back to roughly raw WebP
// size, a ~25% cut on the product's dominant traffic over the office's shared uplink. Requests
// without a Content-Encoding header are untouched, so older agents keep working.
builder.Services.AddRequestDecompression();

// §ExportAPI — the read-only /api/v2 export surface. Entirely separate from the product's JWT
// paths: its own credential type, its own query service, its own rate-limit policy.
builder.Services.AddScoped<IApiClientAuthenticator, ApiClientAuthenticator>();
builder.Services.AddScoped<IApiClientAdminService, ApiClientAdminService>();
builder.Services.AddScoped<IExportQueryService, ExportQueryService>();

// B9 — a hard ceiling under the per-image header check, so a decode that slips past it still
// cannot take the process down on §15.2 hardware.
SixLabors.ImageSharp.Configuration.Default.MemoryAllocator =
    MemoryAllocator.Create(new MemoryAllocatorOptions { AllocationLimitMegabytes = 256 });

var app = builder.Build();

// One-shot maintenance entry, deliberately NOT an HTTP route: there is no way to mint an export
// credential over the network, only from a shell on the server.
//   dotnet Teamscop.Api.dll --issue-api-key "<company name>" "<label>"
// Prints the key and secret ONCE — the secret is stored only as an Argon2 hash and cannot be
// recovered afterwards. Re-running replaces the credential for that company.
if (args.Contains("--issue-api-key"))
{
    return await ExportCredentialCli.RunAsync(app.Services, args);
}
app.UseForwardedHeaders();
app.UseRequestDecompression();

// The one place an exception becomes a status code. Endpoint handlers carry no try/catch.
app.UseTeamscopExceptionHandling();

var dbOpts = app.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (!connectionString.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
    {
        if (dbOpts.MigrateOnStartup)
        {
            db.Database.Migrate();
        }
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
var screenshotRoot = Path.IsPathRooted(storage.ScreenshotRoot)
    ? storage.ScreenshotRoot
    : Path.Combine(app.Environment.ContentRootPath, storage.ScreenshotRoot);
Directory.CreateDirectory(screenshotRoot);

// B12 — no static file middleware. Avatars are staff data and are served, authenticated and
// scoped, by MapMediaEndpoints; nothing under the storage roots is reachable without a token.
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "teamscop-api" }));
app.MapAuthEndpoints().RequireRateLimiting("auth");
app.MapLifecycleEndpoints();
app.MapIngestEndpoints().RequireRateLimiting("api");
app.MapTrackingEndpoints().RequireRateLimiting("api");
app.MapBusinessTimeEndpoints().RequireRateLimiting("api");
app.MapTeamEndpoints();
app.MapPoliceEndpoints().RequireRateLimiting("api");
app.MapMediaEndpoints();
app.MapExportEndpoints();
app.MapHub<ConfigHub>("/hubs/config");

app.Run();
return 0;

public partial class Program;
