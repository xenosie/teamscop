using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Sync;
using Teamscop.Engine.Tracking;
using Teamscop.Engine.Usb;
using Teamscop.StaffService;

// SCM starts services with cwd = System32. Force content root to the exe folder so
// appsettings.Production.json next to Teamscop.StaffService.exe is actually loaded.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = ServiceInstallerHints.ServiceName;
});
builder.Services.AddSystemd();

var apiBase = builder.Configuration["Agent:ApiBaseUrl"] ?? "https://teamscop.com";
var healthUrl = apiBase.TrimEnd('/') + "/health";
var companyKey = builder.Configuration["Agent:CompanyTokenKey"] ?? CompanyTokenKey.Base64;
// Do not throw here — an exception before host.Run() makes SCM report error 1053
// ("service did not respond in a timely fashion"). Validate in the worker after start.

builder.Services.AddSingleton(_ => new LocalAgentStore(AgentRole.Staff));
builder.Services.AddSingleton(_ => new LifecycleApiClient(apiBase));
builder.Services.AddSingleton<IConnectivityProbe>(_ =>
{
    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    return new ConnectivityProbe(http, healthUrl);
});

// One client for every config pull. The previous code created and disposed an HttpClient per
// pull, which throws away the connection pool every three minutes.
builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(15) });

builder.Services.AddSingleton<IOutboxQueue>(sp => new FileOutboxQueue(AgentPaths.Root(sp)));
builder.Services.AddSingleton<ISyncApiClient>(_ => new SyncApiClient(apiBase));
builder.Services.AddSingleton(sp =>
{
    var options = new SyncEngineOptions
    {
        BatchSize = builder.Configuration.GetValue("Agent:BatchSize", 50)
    };
    return new SyncEngine(
        sp.GetRequiredService<IConnectivityProbe>(),
        sp.GetRequiredService<IOutboxQueue>(),
        sp.GetRequiredService<ISyncApiClient>(),
        options);
});

builder.Services.AddSingleton(sp =>
{
    var state = sp.GetRequiredService<LocalAgentStore>().Load();
    var deviceKey = state.DeviceKey ?? new DeviceKeyProvider().GetDeviceKey();
    return new SecureVault(AgentPaths.Root(sp), SecureVault.DeriveMasterKey(deviceKey, companyKey));
});
builder.Services.AddSingleton<BusinessClock>();
builder.Services.AddSingleton(_ => new ConfigRealtimeClient(apiBase));
// The coordinator no longer captures anything itself — no ChromeHistoryWatcher, no in-process
// TimeTrackEngine or ScreenshotEngine. Those all require a desktop or a user profile the session-0
// service does not have, and pretending otherwise is what produced the wall of fabricated idle.
builder.Services.AddSingleton(sp => new TrackingCoordinator(
    sp.GetRequiredService<SecureVault>(),
    sp.GetRequiredService<IOutboxQueue>(),
    businessClock: sp.GetRequiredService<BusinessClock>(),
    // §6.3 — persist the company zone on every change so offline code derivation (a restarted
    // service, the separate uninstall guard) uses the last-known zone, not UTC.
    onBusinessClockChanged: cfg => BusinessClockStore.Save(AgentPaths.Root(sp), cfg)));
builder.Services.AddSingleton(sp => new SessionHelperSupervisor(
    sp.GetRequiredService<TrackingCoordinator>(),
    sp.GetRequiredService<ILogger<SessionHelperSupervisor>>()));
builder.Services.AddSingleton(sp => new CapturePipeServer(
    sp.GetRequiredService<TrackingCoordinator>(),
    sp.GetRequiredService<ILogger<CapturePipeServer>>()));
builder.Services.AddSingleton(sp => new AgentHealthWriter(AgentPaths.Root(sp)));

// §6 — no stored secret, no enrolment: the verifier DERIVES this machine's code from its device key
// (the single shared TotpGenerator.DeriveMachineSecret) on company-local time, so USB and uninstall
// verify with no server call (§6.1, §6.3, §6.4). The company zone comes from the live business
// clock, which the coordinator keeps current and persists for offline restarts.
builder.Services.AddSingleton<IApprovalCodeVerifier>(sp =>
{
    var store = sp.GetRequiredService<LocalAgentStore>();
    var tracking = sp.GetRequiredService<TrackingCoordinator>();
    return new LocalApprovalVerifier(
        deviceKey: () => store.Load().DeviceKey,
        stateDirectory: AgentPaths.Root(sp),
        companyNow: () => new DateTimeOffset(tracking.BusinessClock.Now().BusinessLocal, TimeSpan.Zero));
});

builder.Services.AddSingleton(sp =>
{
    var store = sp.GetRequiredService<LocalAgentStore>();
    var root = AgentPaths.Root(sp);
    var helper = Path.Combine(AppContext.BaseDirectory, "Teamscop.UsbApproval.exe");
    if (!File.Exists(helper))
    {
        helper = Path.Combine(AppContext.BaseDirectory, "Teamscop.UsbApproval");
    }

    var tracking = sp.GetRequiredService<TrackingCoordinator>();
    return new UsbSessionController(
        UsbSessionController.CreateFloor(),
        UsbSessionController.CreateWatcher(),
        new FileUsbApprovalPrompt(Path.Combine(root, "usb"), File.Exists(helper) ? helper : null),
        new LocalUsbAccessVerifier(sp.GetRequiredService<IApprovalCodeVerifier>()),
        deviceKey: () => store.Load().DeviceKey,
        apiBase: () => store.Load().ApiBaseUrl ?? apiBase,
        gate: UsbSessionController.CreateGate(),
        outbox: sp.GetRequiredService<IOutboxQueue>(),
        businessStamp: () =>
        {
            var biz = tracking.BusinessClock.Now();
            return (biz.BusinessLocalIso, biz.TimeZoneId, biz.ClockVersion, biz.Synchronized);
        });
});

builder.Services.AddHostedService<StaffAgentWorker>();

var host = builder.Build();

// §6.3 — seed the live clock with the last-known company zone before the first config pull, so a
// machine that boots offline still derives approval codes on company-local time from the first
// second rather than on UTC until the network returns.
{
    var tracking = host.Services.GetRequiredService<TrackingCoordinator>();
    tracking.ApplyBusinessClock(BusinessClockStore.Load(AgentPaths.Root(host.Services)));
}

host.Run();

namespace Teamscop.StaffService
{
    /// <summary>Where agent state lives. One definition, so vault, outbox and secrets cannot drift apart.</summary>
    internal static class AgentPaths
    {
        public static string Root(IServiceProvider sp)
            => Path.GetDirectoryName(sp.GetRequiredService<LocalAgentStore>().StatePath)
               ?? Path.Combine(
                   Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                   "Teamscop",
                   "Agent");
    }
}
