using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using Serilog;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using TotallyHot.ArcRouter.Gui.Platforms.Windows;
using TotallyHot.ArcRouter.Gui.Services;

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
    /// Builds the MAUI app: starts Serilog, redirects WebView2's user-data folder somewhere writable,
    /// registers the BlazorWebView, and hooks the Windows lifecycle so the main window becomes a
    /// tray-resident window at creation time. Charts use vendored Apache ECharts via JS interop (see
    /// wwwroot/js/echarts-interop.js), so there's no chart service to register.
    /// </summary>
    public static MauiApp CreateMauiApp()
    {
        // Logging first, before anything else here can fail. The installed build's blank-dashboard bug
        // left no trace anywhere precisely because the GUI had no sink at all - the Router's log holds
        // only the service's own entries - so every statement below this one is now diagnosable.
        Log.Logger = GuiLogging.CreateDefaultLogger();
        HookProcessWideFailureHandlers();
        LogStartupEnvironment();

        // Must run before the first BlazorWebView is created: the WebView2 loader reads
        // WEBVIEW2_USER_DATA_FOLDER once, when that view creates its environment, and the default
        // location is unwritable in the installed layout - see WebViewUserData for the whole story.
        ApplyWebViewUserDataFolder();

        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // Routes everything logged through Microsoft.Extensions.Logging into the same Serilog file:
        // the GUI's own ILogger<T>-injected services, and - the reason this matters most - .NET MAUI's
        // WebView2 host, whose "Failed to create WebView2 environment" error is what a blank dashboard
        // window actually looks like from the inside. dispose: false because Log.CloseAndFlush on
        // process exit already owns the logger's lifetime.
        builder.Logging.AddSerilog(Log.Logger, dispose: false);

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
        // Backs the Sessions tab's persisted-history view (docs/router/sessions-tab-training-data-plan.md
        // Phase 2). A singleton for the same reason, sharing the TLS gRPC port (5002) with LiveDataStore.
        // See Services/PersistedSessionStore.cs.
        builder.Services.AddSingleton<PersistedSessionStore>();
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
        // Backs the System Settings window's Software Update section (docs/router/auto-update-plan.md
        // Phase 2). A singleton for the same reason, sharing the TLS gRPC port (5002) with the stores
        // above. See Services/UpdateStore.cs.
        builder.Services.AddSingleton<UpdateStore>();
        // Backs the Model Distribution / Cost Analytics history / header ticker's real data (Phase 4,
        // §5.15). A singleton so its range-keyed cache survives tab switches; talks to the proxy's
        // /admin/usage API (port 5001), same as ProviderAdminStore. See Services/UsageStore.cs.
        builder.Services.AddSingleton<UsageStore>();
        // Backs the tray icon's "Enable Routing"/"Disable Routing" toggle and its service-down detection
        // (right-click while the router is unreachable shows a toast instead of the menu). A singleton,
        // like every store above, but unlike them it polls continuously in the background rather than
        // loading once per component - see Services/RoutingGateStore.cs. TrayWindowManager resolves this
        // from the MAUI service provider when the window is attached, since it has no DI of its own.
        builder.Services.AddSingleton<RoutingGateStore>();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        builder.ConfigureLifecycleEvents(events =>
            events.AddWindows(windows =>
                windows.OnWindowCreated(TrayWindowManager.Attach)));

        var app = builder.Build();
        Log.Information("MAUI app built; handing control to the Windows lifecycle.");
        return app;
    }

    /// <summary>
    /// Records what the process is and where it is running from. Every field here has been the answer to
    /// a "which build is this and why does it behave differently than mine" question: the installed build
    /// runs from %ProgramFiles% and auto-starts from a registry Run key, so neither its version nor its
    /// working directory can be assumed from how a developer launches it.
    /// </summary>
    private static void LogStartupEnvironment() =>
        Log.Information(
            "GUI starting. Version {Version}, process {ProcessId}, user {UserName}, base directory {BaseDirectory}, working directory {WorkingDirectory}, OS {OperatingSystem}.",
            typeof(MauiProgram).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            Environment.ProcessId,
            Environment.UserName,
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
            Environment.OSVersion.VersionString);

    /// <summary>
    /// Points WebView2 at a writable per-user folder and logs where that landed. The failure is caught
    /// rather than thrown: a GUI that cannot create the folder still starts (and still shows its tray
    /// icon, from which the user can quit), whereas an exception here would kill the process during
    /// <c>CreateMauiApp</c> with no window and - before this method existed - no explanation.
    /// </summary>
    private static void ApplyWebViewUserDataFolder()
    {
        try
        {
            Log.Information("WebView2 user-data folder resolved to {UserDataFolder}.", WebViewUserData.Apply());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Error(
                ex,
                "Could not prepare the WebView2 user-data folder; WebView2 will fall back to its default location beside the executable and the dashboard may render blank.");
        }
    }

    /// <summary>
    /// Subscribes the process-wide failure and shutdown hooks. These are what turn "the GUI just
    /// disappeared" into a log entry: a MAUI app that dies outside the UI thread's exception handler
    /// (see <see cref="WinUI.App"/> for that one) otherwise leaves nothing behind at all.
    /// </summary>
    private static void HookProcessWideFailureHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>Logs a fatal unhandled exception and flushes, since the runtime is about to end the process.</summary>
    /// <param name="sender">The app domain raising the event; unused.</param>
    /// <param name="e">Carries the exception and whether the runtime is terminating.</param>
    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Fatal(
            e.ExceptionObject as Exception,
            "Unhandled exception reached the app domain. Runtime terminating: {IsTerminating}.",
            e.IsTerminating);
        Log.CloseAndFlush();
    }

    /// <summary>
    /// Logs a faulted task nobody awaited and marks it observed, so a background failure in one of the
    /// polling stores is recorded instead of being escalated into a process kill by the finalizer thread.
    /// </summary>
    /// <param name="sender">The task scheduler raising the event; unused.</param>
    /// <param name="e">Carries the unobserved exception.</param>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "A background task faulted with nobody awaiting it.");
        e.SetObserved();
    }

    /// <summary>Flushes buffered log events at process exit, so the last thing that happened is on disk.</summary>
    /// <param name="sender">The app domain raising the event; unused.</param>
    /// <param name="e">Empty event arguments; unused.</param>
    private static void OnProcessExit(object? sender, EventArgs e)
    {
        Log.Information("GUI process exiting.");
        Log.CloseAndFlush();
    }
}

