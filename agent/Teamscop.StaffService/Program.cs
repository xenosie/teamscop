using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Sync;
using Teamscop.Engine.Tracking;
using Teamscop.Engine.Usb;
using Teamscop.StaffService;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = ServiceInstallerHints.ServiceName;
});
builder.Services.AddSystemd();

var apiBase = builder.Configuration["Agent:ApiBaseUrl"] ?? "https://teamscop.com";
var healthUrl = apiBase.TrimEnd('/') + "/health";
var companyKey = builder.Configuration["Agent:CompanyTokenKey"] ?? CompanyTokenKey.Base64;

builder.Services.AddSingleton(_ => new LocalAgentStore(AgentRole.Staff));
builder.Services.AddSingleton(_ => new LifecycleApiClient(apiBase));
builder.Services.AddSingleton<IConnectivityProbe>(_ =>
{
    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    return new ConnectivityProbe(http, healthUrl);
});
builder.Services.AddSingleton<IOutboxQueue>(sp =>
{
    var store = sp.GetRequiredService<LocalAgentStore>();
    var root = Path.GetDirectoryName(store.StatePath)
               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Teamscop", "Agent");
    return new FileOutboxQueue(root);
});
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
    var store = sp.GetRequiredService<LocalAgentStore>();
    var root = Path.GetDirectoryName(store.StatePath)
               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Teamscop", "Agent");
    var state = store.Load();
    var deviceKey = state.DeviceKey ?? new DeviceKeyProvider().GetDeviceKey();
    var master = SecureVault.DeriveMasterKey(deviceKey, companyKey);
    return new SecureVault(root, master);
});
builder.Services.AddSingleton(sp =>
{
    var store = sp.GetRequiredService<LocalAgentStore>();
    var root = Path.GetDirectoryName(store.StatePath)
               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Teamscop", "Agent");
    return new ChromeHistoryWatcher(root);
});
builder.Services.AddSingleton<BusinessClock>();
builder.Services.AddSingleton<ConfigRealtimeClient>(_ => new ConfigRealtimeClient(apiBase));
builder.Services.AddSingleton(sp => new TrackingCoordinator(
    sp.GetRequiredService<SecureVault>(),
    sp.GetRequiredService<IOutboxQueue>(),
    sp.GetRequiredService<ChromeHistoryWatcher>(),
    businessClock: sp.GetRequiredService<BusinessClock>()));

builder.Services.AddSingleton(sp => new AppBrokenWatchdog(
    AppContext.BaseDirectory,
    sp.GetRequiredService<IOutboxQueue>()));

builder.Services.AddSingleton(sp =>
{
    var store = sp.GetRequiredService<LocalAgentStore>();
    var root = Path.GetDirectoryName(store.StatePath)
               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Teamscop", "Agent");
    var helper = Path.Combine(AppContext.BaseDirectory, "Teamscop.UsbApproval.exe");
    if (!File.Exists(helper))
    {
        helper = Path.Combine(AppContext.BaseDirectory, "Teamscop.UsbApproval");
    }

    var lifecycle = sp.GetRequiredService<LifecycleApiClient>();
    return new UsbSessionController(
        UsbSessionController.CreatePolicy(),
        new PollingUsbDeviceWatcher(),
        new FileUsbApprovalPrompt(Path.Combine(root, "usb"), File.Exists(helper) ? helper : null),
        new LifecycleUsbAccessVerifier(lifecycle),
        deviceKey: () => store.Load().DeviceKey,
        apiBase: () => store.Load().ApiBaseUrl ?? apiBase,
        outbox: sp.GetRequiredService<IOutboxQueue>());
});

builder.Services.AddHostedService<StaffAgentWorker>();

var host = builder.Build();
host.Run();
