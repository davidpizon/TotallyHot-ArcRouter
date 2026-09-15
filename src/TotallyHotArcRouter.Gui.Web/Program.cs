using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using System.Diagnostics.CodeAnalysis;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;
using TotallyHot.ArcRouter.Gui.Web;

// Excluded from code coverage (see the csproj's matching note): this only wires a WebAssemblyHost
// together and requires a live browser WASM runtime to execute at all - the services it registers
// are unit-tested independently, the same posture TotallyHotArcRouter.Gui/MauiProgram.cs takes for its
// own composition root.
[assembly: ExcludeFromCodeCoverage]

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Dashboard>("#root");

// Local, per-user GUI settings. Reused as-is from the native host (web GUI migration plan Phase P5a) -
// its file-backed store harmlessly no-ops to an in-memory, per-session default here (WASM has no
// persistent filesystem without an explicit virtual-FS mount), which is the correct behavior for this
// host anyway: the telemetry address it would otherwise persist is moot in the browser, which is always
// same-origin - see WasmRouterChannelProvider's remarks.
builder.Services.AddSingleton<IGuiSettingsStore>(_ => new GuiSettingsStore());
// App-wide error-toast notifications (see Services/ToastService.cs and Components/ToastHost.razor).
builder.Services.AddSingleton<ToastService>();
// navigator.clipboard.writeText via JS interop - the browser counterpart to MauiProgram's
// MauiClipboardService registration.
builder.Services.AddSingleton<IClipboardService, WasmClipboardService>();
// The one shared, authenticated call invoker every admin client and store talks through (web GUI
// migration plan Phase P5a/P6) - gRPC-Web to this same origin, unlike the native host's real TCP+TLS
// channel. See WasmRouterChannelProvider's remarks.
builder.Services.AddSingleton<IRouterChannelProvider>(sp =>
    new WasmRouterChannelProvider(sp.GetRequiredService<NavigationManager>()));
// Live routing telemetry from the router (see Services/LiveDataStore.cs). A singleton so the gRPC-Web
// stream and accumulated conversation state survive navigation between tabs.
builder.Services.AddSingleton<LiveDataStore>();
// Backs the Governance tab's provider/credential/model manager (see Services/ProviderAdminStore.cs). No
// adminToken: this host authenticates via the ADR-0012 session cookie the browser sends automatically
// on every same-origin call, not a management-token header - see ProviderAdminStore's remarks.
builder.Services.AddSingleton<ProviderAdminStore>();
// Backs the Sessions tab's persisted-history view. See Services/PersistedSessionStore.cs.
builder.Services.AddSingleton<PersistedSessionStore>();
// Backs the Governance tab's price-source panel. See Services/PriceSourceStore.cs.
builder.Services.AddSingleton<PriceSourceStore>();
// Backs the Governance tab's Benchmark Data panel. See Services/BenchmarkDataStore.cs.
builder.Services.AddSingleton<BenchmarkDataStore>();
// Backs the Benchmark Data panel's "Local Voter Model" section. See Services/LlmRouterModelStore.cs.
builder.Services.AddSingleton<LlmRouterModelStore>();
// Backs the Governance tab's read-only Routing Mode panel. See Services/RoutingModeStore.cs.
builder.Services.AddSingleton<RoutingModeStore>();
// Backs the Governance tab's Cluster Model panel. See Services/ClusterModelAdminStore.cs.
builder.Services.AddSingleton<ClusterModelAdminStore>();
// Backs the Governance tab's Router Model panel. See Services/LogRegModelAdminStore.cs.
builder.Services.AddSingleton<LogRegModelAdminStore>();
// Backs the Governance tab's Regret Harness panel. See Services/RegretHarnessAdminStore.cs.
builder.Services.AddSingleton<RegretHarnessAdminStore>();
// Backs the Governance tab's Judge Calibration panel. See Services/JudgeCalibrationAdminStore.cs.
builder.Services.AddSingleton<JudgeCalibrationAdminStore>();
// Backs the System Settings window's Adaptive Routing row. See Services/RouterSettingsAdminStore.cs.
builder.Services.AddSingleton<RouterSettingsAdminStore>();
// Backs the System Settings window's Software Update section (read-only status/check in this host -
// see UpdateStore's remarks on ApplyAsync not being wired to any UI action here). See
// Services/UpdateStore.cs.
builder.Services.AddSingleton<UpdateStore>();
// Backs the System Settings window's Cost Reconciliation section. See Services/CostReconciliationStore.cs.
builder.Services.AddSingleton<CostReconciliationStore>();
// Backs the Model Distribution / Cost Analytics history / header ticker's real data. See
// Services/UsageStore.cs.
builder.Services.AddSingleton<UsageStore>();

var host = builder.Build();

// Session bootstrap (ADR-0012, web GUI migration plan Phase P6): a same-machine browser tab gets a
// session cookie with no credential prompt via the loopback fast path. Best-effort and fire-once ahead
// of the first render - a failure here (a non-loopback caller, or WebInterface:TrustLoopback=false)
// just means the first admin/telemetry call each store makes comes back Unauthenticated instead, which
// every store already renders as its ordinary "router unreachable" state; there is no dedicated login
// view yet (see the migration plan's P6 status notes for that gap).
try
{
    using var sessionClient = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
    await sessionClient.PostAsync(requestUri: "auth/session", content: null).ConfigureAwait(false);
}
catch (HttpRequestException)
{
    // The router isn't reachable yet, or refused the loopback check - each store's own reachability
    // handling covers the user-facing side of this; nothing further to do at bootstrap time.
}

await host.RunAsync();
