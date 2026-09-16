using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using System.Diagnostics.CodeAnalysis;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;
using TotallyHot.ArcRouter.Gui.Web;

// Excluded from code coverage (see the csproj's matching note): this only wires a WebAssemblyHost
// together and requires a live browser WASM runtime to execute at all - the services it registers
// are unit-tested independently, the same posture TotallyHotArcRouter.Gui/MauiProgram.cs takes for its
// own composition root.
[assembly: ExcludeFromCodeCoverage]

var builder = WebAssemblyHostBuilder.CreateDefault(args);
// AppRoot, not Dashboard directly - it gates the dashboard behind a token-login form when the startup
// session bootstrap below determines one is required. See AppRoot.razor/AuthGateState.cs.
builder.RootComponents.Add<AppRoot>("#root");
builder.Services.AddSingleton<AuthGateState>();

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
// Backs the System Settings window's Software Update section. supportsApply: false - this host runs
// sandboxed inside a browser tab and cannot launch the downloaded MSI itself (D11, web GUI migration
// plan); SettingsModal hides "Apply Update" and links to the release page instead. See
// Services/UpdateStore.cs's SupportsApply remarks.
builder.Services.AddSingleton(sp =>
    new UpdateStore(channelProvider: sp.GetRequiredService<IRouterChannelProvider>(), supportsApply: false));
// Backs the System Settings window's Cost Reconciliation section. See Services/CostReconciliationStore.cs.
builder.Services.AddSingleton<CostReconciliationStore>();
// Backs the System Settings window's "Copy MCP token / Regenerate" row (web GUI migration plan Phase P9).
// See Services/ManagementTokenAdminStore.cs. reauthenticateAsync re-issues this tab's own session cookie
// immediately after a successful Regenerate - rotation bumps the token's Generation and invalidates every
// outstanding session ticket, including the one this same tab is using, so without this the tab that just
// clicked Regenerate would lock itself out of every further management call until a manual page reload.
builder.Services.AddSingleton(sp => new ManagementTokenAdminStore(
    channelProvider: sp.GetRequiredService<IRouterChannelProvider>(),
    reauthenticateAsync: _ => PostAuthSessionAsync(sp.GetRequiredService<NavigationManager>().BaseUri)));
// Backs the Model Distribution / Cost Analytics history / header ticker's real data. See
// Services/UsageStore.cs.
builder.Services.AddSingleton<UsageStore>();

var host = builder.Build();

// Session bootstrap (ADR-0012, web GUI migration plan Phase P6): a same-machine browser tab gets a
// session cookie with no credential prompt via the loopback fast path. Best-effort and fire-once ahead
// of the first render. A definite 403 (the router is reachable but refused the loopback fast path - a
// non-loopback caller such as Docker's default WebInterface__BindAddress=0.0.0.0, or an operator who set
// WebInterface:TrustLoopback=false) flips AuthGateState.LoginRequired so AppRoot shows the token-login
// form instead of the dashboard (Phase P11 - the migration plan's P6 status notes had named the absence
// of this as a known gap). A network-level failure (the router isn't reachable at all yet) leaves it
// false: a login form would be misleading there, since no token would help - each store's own ordinary
// "unreachable" state already covers that case. ManagementTokenAdminStore's own reauthenticateAsync
// registration above calls the same PostAuthSessionAsync helper again after a Regenerate, for the
// session-invalidation reason described in that store's own remarks.
try
{
    using var response = await PostAuthSessionAsync(builder.HostEnvironment.BaseAddress).ConfigureAwait(false);
    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        host.Services.GetRequiredService<AuthGateState>().LoginRequired = true;
}
catch (HttpRequestException)
{
    // The router isn't reachable at all yet - nothing further to do at bootstrap time.
}

await host.RunAsync();

// Shared by the startup bootstrap above and ManagementTokenAdminStore's reauthenticateAsync registration:
// issues (or re-issues) this tab's ADR-0012 loopback session cookie via POST {baseAddress}/auth/session.
// A short-lived HttpClient is deliberately created per call rather than injected - this runs both before
// and after the DI container is fully wired up (the bootstrap call happens right after builder.Build(),
// the reauthenticateAsync call happens on demand, much later, from inside a DI factory), so there is no
// single natural place to register a long-lived one that both call sites could share.
static async Task<HttpResponseMessage> PostAuthSessionAsync(string baseAddress)
{
    // Must await inside this method's own using block, not return the unawaited Task from it: disposing
    // sessionClient happens synchronously as this method returns, which - for a non-async method just
    // returning PostAsync's Task directly - happened before the in-flight request actually completed
    // (a real, latent bug this rewrite also fixes, found while touching this function for AppRoot's
    // sake: HttpClient.Dispose() while a request is in flight can abort it under its default handler).
    using var sessionClient = new HttpClient { BaseAddress = new Uri(baseAddress) };
    return await sessionClient.PostAsync(requestUri: "auth/session", content: null).ConfigureAwait(false);
}
