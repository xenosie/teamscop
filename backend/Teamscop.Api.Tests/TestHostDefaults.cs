using System.Runtime.CompilerServices;

namespace Teamscop.Api.Tests;

internal static class TestHostDefaults
{
    /// <summary>
    /// Every fixture in this suite builds its own host, and every host installs inotify watchers
    /// on its JSON configuration files. Linux allows 128 instances per user by default, which the
    /// suite now exceeds — hosts start failing to start with an IOException that has nothing to do
    /// with the test that happens to be running. Nothing here reloads configuration mid-run, so
    /// the watchers are pure cost.
    ///
    /// Set as an environment variable rather than per factory: two thirds of the classes use a
    /// bare <c>WebApplicationFactory&lt;Program&gt;</c> with no hook to configure.
    /// </summary>
    [ModuleInitializer]
    internal static void DisableConfigurationFileWatchers()
        => Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");
}
