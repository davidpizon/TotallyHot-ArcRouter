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
/// <see cref="TrayStatusPresenter"/>; every router connection/reconnection decision is delegated to
/// <see cref="RouterConnectionSupervisor"/>; every routing-gate/update interaction goes through the
/// monitor/coordinator it hands back - this class is deliberately thin glue, matching AGENTS.md's
/// <c>[ExcludeFromCodeCoverage]</c> convention for platform shells whose logic has already been extracted
/// and tested elsewhere.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class TrayApplicationContext : ApplicationContext
{
    private const string ServiceName = "TotallyHotArcRouter";
    private static readonly TimeSpan ServiceStatusPollInterval = TimeSpan.FromSeconds(3);

    private readonly ToolStripMenuItem _installUpdateItem;
    private readonly IMsiUpdateApplier _msiUpdateApplier;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _routingToggleItem;
    private readonly ToolStripMenuItem _statusCaptionItem;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly RouterConnectionSupervisor _supervisor;

    /// <summary>
    /// Builds the tray icon and starts its background connection supervisor. If the router has never
    /// started (no discovery file yet), the icon still appears - the menu just reports the router as
    /// unreachable, and <see cref="RouterConnectionSupervisor"/> keeps retrying, rather than the whole
    /// tray failing to launch.
    /// </summary>
    public TrayApplicationContext()
    {
        var discovery = TrayDiscoveryReader.TryRead();
        var serverAddress = discovery?.WebUrl ?? TelemetryChannelFactory.DefaultServerAddress;

        // ADR-0012 loopback session cookie, not the shared x-admin-token file: the tray always runs on the
        // same machine as the router, so it qualifies for the loopback fast path with no credential of its
        // own to manage - see RouterConnectionSupervisor's remarks for why a *supervisor* (not a one-shot
        // connect) is needed: the session is only as durable as the router process, so a router restart
        // needs to be noticed and re-authenticated automatically.
        _supervisor = new RouterConnectionSupervisor(new SessionRouterConnector(), serverAddress);
        _supervisor.Reconnected += OnReconnected;

        _msiUpdateApplier = new MsiUpdateApplier(httpClient: new HttpClient(),
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<MsiUpdateApplier>.Instance);

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
        var monitor = _supervisor.Monitor;
        var serviceStatus = TryGetServiceStatus();
        var connectionState = monitor?.ConnectionState ?? RouterConnectionState.Unreachable;
        var isUsable = monitor?.IsUsable ?? false;

        _statusCaptionItem.Visible = !isUsable;
        _statusCaptionItem.Text = TrayStatusPresenter.BuildStatusLabel(serviceStatus, connectionState);

        if (isUsable)
        {
            _routingToggleItem.Enabled = true;
            _routingToggleItem.Text = monitor!.IsEnabled ? "Disable Routing" : "Enable Routing";
        }
        else
        {
            _routingToggleItem.Enabled = false;
            _routingToggleItem.Text = "Routing Unavailable";
        }
    }

    /// <summary>
    /// Re-subscribes <see cref="OnRoutingGateBecameUnusable"/> to the fresh
    /// <see cref="RouterConnectionSupervisor.Monitor"/> a reconnect just installed - the previous
    /// instance's own subscription died with it - and refreshes the menu immediately rather than waiting
    /// for the next timer tick, so a reconnect after a router restart is reflected right away.
    /// </summary>
    private void OnReconnected()
    {
        if (_supervisor.Monitor is { } monitor) monitor.BecameUnusable += OnRoutingGateBecameUnusable;

        RefreshMenuState();
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
        var connectionState = _supervisor.Monitor?.ConnectionState ?? RouterConnectionState.Unreachable;

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
        var monitor = _supervisor.Monitor;
        if (monitor is null || !monitor.IsUsable) return;

        var enable = !monitor.IsEnabled;
        _ = ToggleRoutingAsync(monitor, enable);
    }

    private async Task ToggleRoutingAsync(RoutingGateMonitor monitor, bool enable)
    {
        try
        {
            if (enable) await monitor.EnableAsync().ConfigureAwait(true);
            else await monitor.DisableAsync().ConfigureAwait(true);
        }
        catch (GrpcAdminException)
        {
            // Swallowed: the next poll tick (or the next menu open) re-reads the actual state and the
            // menu reflects reality either way - matching TrayWindowManager's own toggle error handling.
        }

        RefreshMenuState();
    }

    /// <summary>
    /// Builds a fresh <see cref="TrayUpdateCoordinator"/> over the supervisor's current connection each
    /// time this runs, rather than holding one for the tray's whole lifetime - the coordinator's
    /// <see cref="UpdateAdminClient"/> is bound to one specific call invoker, which goes stale the moment
    /// <see cref="RouterConnectionSupervisor"/> reconnects (a router restart, most likely - the same event
    /// that invalidates the session cookie an old coordinator's calls would otherwise keep presenting).
    /// </summary>
    private async Task ApplyUpdateAsync()
    {
        if (_supervisor.Provider is not { } provider) return;

        var coordinator = new TrayUpdateCoordinator(client: new UpdateAdminClient(provider.CallInvoker),
            applier: _msiUpdateApplier, exitApplication: ExitApplication);

        try
        {
            var status = await coordinator.CheckNowAsync().ConfigureAwait(true);
            if (!status.UpdateAvailable) return;

            var confirmed = MessageBox.Show(
                text: $"Version {status.LatestVersion} is available. Install it now? The router service will restart.",
                caption: "TotallyHot Arc Router Update", buttons: MessageBoxButtons.YesNo,
                icon: MessageBoxIcon.Question);
            if (confirmed != DialogResult.Yes) return;

            await coordinator.ApplyAsync().ConfigureAwait(true);
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
        _supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Application.Exit();
    }
}
