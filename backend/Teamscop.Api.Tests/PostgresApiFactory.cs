using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Teamscop.Api.Tests;

/// <summary>
/// The API bound to a throwaway PostgreSQL database. Startup runs <c>Database.Migrate()</c>, so
/// every class using this fixture also replays the whole migration chain from empty — the check
/// nothing else in the suite performs.
/// </summary>
public class PostgresApiFactory : WebApplicationFactory<Program>
{
    private readonly string? _connectionString = PostgresTestServer.CreateScratchDatabaseOrSkip();

    private readonly string _screenshotRoot =
        Path.Combine(Path.GetTempPath(), "teamscop-pg-blobs-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Storage:ScreenshotRoot", _screenshotRoot);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        if (_connectionString is not null)
        {
            PostgresTestServer.DropScratchDatabase(_connectionString);
        }

        if (Directory.Exists(_screenshotRoot))
        {
            Directory.Delete(_screenshotRoot, recursive: true);
        }
    }
}
