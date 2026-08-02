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
/// <see cref="PriceSourceStore"/>) are unit-tested directly instead.
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
        // Live routing telemetry from the TotallyHot.ArcRouter proxy (see Services/LiveDataStore.cs). A
        // singleton so the gRPC stream and accumulated conversation state survive navigation between
        // tabs; Dashboard.razor starts the connection on first render.
        builder.Services.AddSingleton<LiveDataStore>();
        // Backs the Governance tab's provider/credential/model manager. A singleton so its loaded
        // provider list survives tab switches; it talks to the proxy's /admin API (port 5001) via the
        // tested TotallyHot.ArcRouter.Gui.Admin client. See Services/ProviderAdminStore.cs.
        builder.Services.AddSingleton<ProviderAdminStore>();
        // Backs the Governance tab's price-source panel. A singleton for the same reason, sharing the TLS
        // gRPC port (5002) with LiveDataStore. See Services/PriceSourceStore.cs.
        builder.Services.AddSingleton<PriceSourceStore>();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        builder.ConfigureLifecycleEvents(events =>
            events.AddWindows(windows =>
                windows.OnWindowCreated(TrayWindowManager.Attach)));

        return builder.Build();
    }
}

