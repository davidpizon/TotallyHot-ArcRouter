using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.ServiceProcess;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Tray;

/// <summary>
/// The tray icon's whole lifetime: builds the <see cref="NotifyIcon"/>/<see cref="ContextMenuStrip"/>,
/// polls the Windows service and the router's routing gate, and dispatches every menu action - the
/// WinForms-native counterpart to <c>TotallyHotArcRouter.Gui</c>'s raw-Win32
/// <c>Platforms/Windows/TrayWindowManager.cs</c>, built on <c>NotifyIcon</c>/<c>ContextMenuStrip</c>
/// instead of hand-written P/Invoke since this shell has no MAUI/WebView2 host window to subclass.
/// </summary>
/// <remarks>
/// Every formatting/classification decision (the status caption, the balloon text) is delegated to
/// <see cref="TrayStatusPresenter"/>; every router interaction is delegated to
/// <see cref="RoutingGateMonitor"/>/<see cref="TrayUpdateCoordinator"/> - this class is deliberately thin
/// glue, matching AGENTS.md's <c>[ExcludeFromCodeCoverage]</c> convention for platform shells whose logic
/// has already been extracted and tested elsewhere.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class TrayApplicationContext : ApplicationContext
{
    private const string ServiceName = "TotallyHotArcRouter";
    private static readonly TimeSpan ServiceStatusPollInterval = TimeSpan.FromSeconds(3);

    private readonly ToolStripMenuItem _installUpdateItem;
    private readonly NativeRouterChannelProvider? _channelProvider;
    private readonly IMsiUpdateApplier? _msiUpdateApplier;
    private readonly NotifyIcon _notifyIcon;
    private readonly RoutingGateMonitor? _routingGateMonitor;
    private readonly ToolStripMenuItem _routingToggleItem;
    private readonly ToolStripMenuItem _statusCaptionItem;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly TrayUpdateCoordinator? _updateCoordinator;

    /// <summary>
    /// Builds the tray icon and starts its background pollers. If the router has never started (no
    /// discovery file yet), the icon still appears - the menu just reports the router as unreachable
    /// rather than the whole tray failing to launch.
    /// </summary>
    public TrayApplicationContext()
    {
        var discovery = TrayDiscoveryReader.TryRead();
        var serverAddress = discovery?.WebUrl ?? TelemetryChannelFactory.DefaultServerAddress;

        try
        {
            _channelProvider = new NativeRouterChannelProvider(serverAddress);
            _routingGateMonitor = new RoutingGateMonitor(_channelProvider);
            _routingGateMonitor.BecameUnusable += OnRoutingGateBecameUnusable;

            _msiUpdateApplier = new MsiUpdateApplier(httpClient: new HttpClient(),
                logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<MsiUpdateApplier>.Instance);
            _updateCoordinator = new TrayUpdateCoordinator(
                client: new UpdateAdminClient(_channelProvider.CallInvoker),
                applier: _msiUpdateApplier,
                exitApplication: ExitApplication);
        }
        catch (UriFormatException)
        {
            // A malformed WebUrl in the discovery file (a stale/corrupt write mid-crash) - fall back to
            // an unreachable-router tray rather than crash the whole process on startup.
        }

        _statusCaptionItem = new ToolStripMenuItem("Router: not responding") { Enabled = false };
        _routingToggleItem = new ToolStripMenuItem("Routing Unavailable") { Enabled = false };
        _routingToggleItem.Click += (_, _) => ToggleRouting();
        _installUpdateItem = new ToolStripMenuItem("Install update") { Visible = false };
        _installUpdateItem.Click += async (_, _) => await ApplyUpdateAsync().ConfigureAwait(true);

        var showDashboardItem = new ToolStripMenuItem("Show Dashboard");
        showDashboardItem.Click += (_, _) => ShowDashboard();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApplication();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusCaptionItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(showDashboardItem);
        menu.Items.Add(_routingToggleItem);
        menu.Items.Add(_installUpdateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        menu.Opening += (_, _) => RefreshMenuState();

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "TotallyHot Arc Router",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowDashboard();

        _statusTimer = new System.Windows.Forms.Timer { Interval = (int)ServiceStatusPollInterval.TotalMilliseconds };
        _statusTimer.Tick += (_, _) => RefreshMenuState();
        _statusTimer.Start();
    }

    /// <summary>
    /// Reads the current Windows service status, or <see langword="null"/> when it can't be queried
    /// (the service isn't installed, or the query itself failed) - mirroring
    /// <c>TrayWindowManager.TryGetServiceStatus</c>'s "never conflate 'can't check' with 'stopped'" rule.
    /// </summary>
    private static RouterServiceStatus? TryGetServiceStatus()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            controller.Refresh();
            return controller.Status switch
            {
                ServiceControllerStatus.Stopped => RouterServiceStatus.Stopped,
                ServiceControllerStatus.StartPending => RouterServiceStatus.StartPending,
                ServiceControllerStatus.StopPending => RouterServiceStatus.StopPending,
                ServiceControllerStatus.Paused => RouterServiceStatus.Paused,
                _ => RouterServiceStatus.Running
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static Icon LoadTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "appicon.ico");
        return File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;
    }

    /// <summary>
    /// Re-reads the service status and routing-gate state and updates the menu's caption/toggle/update
    /// items - called both on a timer and every time the menu is about to open, so a right-click always
    /// shows a fresh answer rather than whatever the last timer tick happened to see.
    /// </summary>
    private void RefreshMenuState()
    {
        var serviceStatus = TryGetServiceStatus();
        var connectionState = _routingGateMonitor?.ConnectionState ?? RouterConnectionState.Unreachable;
        var isUsable = _routingGateMonitor?.IsUsable ?? false;

        _statusCaptionItem.Visible = !isUsable;
        _statusCaptionItem.Text = TrayStatusPresenter.BuildStatusLabel(serviceStatus, connectionState);

        if (isUsable)
        {
            _routingToggleItem.Enabled = true;
            _routingToggleItem.Text = _routingGateMonitor!.IsEnabled ? "Disable Routing" : "Enable Routing";
        }
        else
        {
            _routingToggleItem.Enabled = false;
            _routingToggleItem.Text = "Routing Unavailable";
        }
    }

    /// <summary>
    /// Shows the one-time balloon <see cref="RoutingGateMonitor.BecameUnusable"/> reports - that event
    /// already fires exactly once per usable-to-unusable transition (see its own remarks), so no
    /// additional de-duplication is needed here. Raised on the monitor's background poll thread;
    /// <see cref="NotifyIcon.ShowBalloonTip(int, string, string, ToolTipIcon)"/> posts a shell
    /// notification rather than touching a window handle synchronously, so no explicit marshaling onto
    /// the UI thread is required, unlike a <see cref="Control"/>.
    /// </summary>
    private void OnRoutingGateBecameUnusable()
    {
        var serviceStatus = TryGetServiceStatus();
        var connectionState = _routingGateMonitor?.ConnectionState ?? RouterConnectionState.Unreachable;

        _notifyIcon.ShowBalloonTip(timeout: 10_000, tipTitle: "TotallyHot Arc Router",
            tipText: TrayStatusPresenter.BuildBalloonMessage(serviceStatus, connectionState),
            tipIcon: ToolTipIcon.Warning);
    }

    private void ShowDashboard()
    {
        var discovery = TrayDiscoveryReader.TryRead();
        if (discovery?.WebUrl is not { } webUrl) return;

        Process.Start(new ProcessStartInfo(webUrl) { UseShellExecute = true });
    }

    private void ToggleRouting()
    {
        if (_routingGateMonitor is null || !_routingGateMonitor.IsUsable) return;

        var enable = !_routingGateMonitor.IsEnabled;
        _ = ToggleRoutingAsync(enable);
    }

    private async Task ToggleRoutingAsync(bool enable)
    {
        try
        {
            if (enable) await _routingGateMonitor!.EnableAsync().ConfigureAwait(true);
            else await _routingGateMonitor!.DisableAsync().ConfigureAwait(true);
        }
        catch (GrpcAdminException)
        {
            // Swallowed: the next poll tick (or the next menu open) re-reads the actual state and the
            // menu reflects reality either way - matching TrayWindowManager's own toggle error handling.
        }

        RefreshMenuState();
    }

    private async Task ApplyUpdateAsync()
    {
        if (_updateCoordinator is null) return;

        try
        {
            var status = await _updateCoordinator.CheckNowAsync().ConfigureAwait(true);
            if (!status.UpdateAvailable) return;

            var confirmed = MessageBox.Show(
                text: $"Version {status.LatestVersion} is available. Install it now? The router service will restart.",
                caption: "TotallyHot Arc Router Update", buttons: MessageBoxButtons.YesNo,
                icon: MessageBoxIcon.Question);
            if (confirmed != DialogResult.Yes) return;

            await _updateCoordinator.ApplyAsync().ConfigureAwait(true);
        }
        catch (GrpcAdminException ex)
        {
            MessageBox.Show(text: $"Could not check for updates: {ex.Message}", caption: "TotallyHot Arc Router",
                buttons: MessageBoxButtons.OK, icon: MessageBoxIcon.Warning);
        }
    }

    private void ExitApplication()
    {
        _statusTimer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _routingGateMonitor?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _channelProvider?.Dispose();
        Application.Exit();
    }
}
