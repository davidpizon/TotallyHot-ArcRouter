using System.Diagnostics.CodeAnalysis;
using TotallyHot.ArcRouter.Gui.Platforms.Windows;
using TotallyHot.ArcRouter.Gui.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;

namespace TotallyHot.ArcRouter.Gui;

/// <summary>
/// Composition root for the MAUI Blazor Hybrid app.
/// </summary>
/// <remarks>
/// Excluded from code coverage: <see cref="CreateMauiApp"/> calls <c>MauiAppBuilder.Build()</c>, which
/// requires a live Windows App SDK host to initialize and cannot run inside an xUnit/bUnit process; the
/// services it registers (<see cref="LiveDataStore"/>, <see cref="ProviderAdminStore"/>,
/// <see cref="PriceSourceStore"/>, <see cref="UsageStore"/>) are unit-tested directly instead.
/// </remarks>
[ExcludeFromCodeCoverage]
public static class MauiProgram
{
    /// <summary>
    /// Builds the MAUI app: registers the BlazorWebView and hooks the Windows lifecycle so the main
    /// window becomes a tray-resident window at creation time. Charts use vendored Apache ECharts via
    /// JS interop (see wwwroot/js/echarts-interop.js), so there's no chart service to register.
    /// </summary>
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();
        // Local, per-user GUI settings (currently just the telemetry server address) - see
        // Services/GuiSettingsStore.cs. Registered before LiveDataStore so its factory below can read
        // the persisted address.
        builder.Services.AddSingleton<IGuiSettingsStore>(_ => new GuiSettingsStore());
        // App-wide error-toast notifications (see Services/ToastService.cs and Components/ToastHost.razor).
        // Registered before ProviderAdminStore so DI can inject it there.
        builder.Services.AddSingleton<ToastService>();
        // Live routing telemetry from the TotallyHot.ArcRouter proxy (see Services/LiveDataStore.cs). A
        // singleton so the gRPC stream and accumulated conversation state survive navigation between
        // tabs; Dashboard.razor starts the connection on first render. The server address comes from
        // GuiSettingsStore rather than the hardcoded default, so a change in Settings takes effect on
        // the next launch (the singleton factory below runs once, on first resolution).
        builder.Services.AddSingleton(sp =>
            new LiveDataStore(
                sp.GetRequiredService<ILogger<LiveDataStore>>(),
                sp.GetRequiredService<IGuiSettingsStore>().Load().TelemetryServerAddress));
        // Backs the Governance tab's provider/credential/model manager. A singleton so its loaded
        // provider list survives tab switches; it talks to the proxy's /admin API (port 5001) via the
        // tested TotallyHot.ArcRouter.Gui.Admin client. See Services/ProviderAdminStore.cs.
        builder.Services.AddSingleton<ProviderAdminStore>();
        // Backs the Governance tab's price-source panel. A singleton for the same reason, sharing the TLS
        // gRPC port (5002) with LiveDataStore. See Services/PriceSourceStore.cs.
        builder.Services.AddSingleton<PriceSourceStore>();
        // Backs the Governance tab's Benchmark Data panel. A singleton for the same reason, sharing the
        // TLS gRPC port (5002) with LiveDataStore and PriceSourceStore. See Services/BenchmarkDataStore.cs.
        builder.Services.AddSingleton<BenchmarkDataStore>();
        // Backs the Benchmark Data panel's "Local Voter Model" section. A singleton for the same reason,
        // sharing the TLS gRPC port (5002) with the stores above. See Services/LlmRouterModelStore.cs.
        builder.Services.AddSingleton<LlmRouterModelStore>();
        // Backs the Governance tab's read-only Routing Mode panel. A singleton for the same reason,
        // sharing the TLS gRPC port (5002) with the stores above. See Services/RoutingModeStore.cs.
        builder.Services.AddSingleton<RoutingModeStore>();
        // Backs the Governance tab's Cluster Model panel (Phase T5). A singleton for the same reason,
        // sharing the TLS gRPC port (5002) with the stores above. See Services/ClusterModelAdminStore.cs.
        builder.Services.AddSingleton<ClusterModelAdminStore>();
        // Backs the Governance tab's Router Model panel (live-feedback-learning-plan.md Phase 5). A
        // singleton for the same reason, sharing the TLS gRPC port (5002) with the stores above. See
        // Services/LogRegModelAdminStore.cs.
        builder.Services.AddSingleton<LogRegModelAdminStore>();
        // Backs the System Settings window's Adaptive Routing row (Phase T6). A singleton for the same
        // reason, sharing the TLS gRPC port (5002) with the stores above. See Services/RouterSettingsAdminStore.cs.
        builder.Services.AddSingleton<RouterSettingsAdminStore>();
        // Backs the Model Distribution / Cost Analytics history / header ticker's real data (Phase 4,
        // §5.15). A singleton so its range-keyed cache survives tab switches; talks to the proxy's
        // /admin/usage API (port 5001), same as ProviderAdminStore. See Services/UsageStore.cs.
        builder.Services.AddSingleton<UsageStore>();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        builder.ConfigureLifecycleEvents(events =>
            events.AddWindows(windows =>
                windows.OnWindowCreated(TrayWindowManager.Attach)));

        return builder.Build();
    }
}

