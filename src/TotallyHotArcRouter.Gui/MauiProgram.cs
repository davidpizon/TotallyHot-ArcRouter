using System.Diagnostics.CodeAnalysis;
using TotallyHot.ArcRouter.Gui.Platforms.Windows;
using TotallyHot.ArcRouter.Gui.Services;
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
        // Live routing telemetry from the TotallyHot.ArcRouter proxy (see Services/LiveDataStore.cs). A
        // singleton so the gRPC stream and accumulated conversation state survive navigation between
        // tabs; Dashboard.razor starts the connection on first render. The server address comes from
        // GuiSettingsStore rather than the hardcoded default, so a change in Settings takes effect on
        // the next launch (the channel is constructed once, here, at DI-registration time).
        builder.Services.AddSingleton(sp =>
            new LiveDataStore(serverAddress: sp.GetRequiredService<IGuiSettingsStore>().Load().TelemetryServerAddress));
        // Backs the Governance tab's provider/credential/model manager. A singleton so its loaded
        // provider list survives tab switches; it talks to the proxy's /admin API (port 5001) via the
        // tested TotallyHot.ArcRouter.Gui.Admin client. See Services/ProviderAdminStore.cs.
        builder.Services.AddSingleton<ProviderAdminStore>();
        // Backs the Governance tab's price-source panel. A singleton for the same reason, sharing the TLS
        // gRPC port (5002) with LiveDataStore. See Services/PriceSourceStore.cs.
        builder.Services.AddSingleton<PriceSourceStore>();
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

