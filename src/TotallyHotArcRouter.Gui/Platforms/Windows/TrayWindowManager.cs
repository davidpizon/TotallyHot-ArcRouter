using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;
using Microsoft.UI.Dispatching;

namespace TotallyHot.ArcRouter.Gui.Platforms.Windows;

/// <summary>
/// Makes the MAUI main window tray-resident on Windows. MAUI has no built-in system tray support, so
/// this is implemented directly against Win32: a Shell_NotifyIcon tray icon whose callback messages are
/// received by subclassing the main window's WndProc. The same subclass intercepts minimize and close so
/// both hide the window to the tray instead; only the tray menu's Exit actually quits. The tray menu also
/// carries the "Enable Routing"/"Disable Routing" kill switch, backed by <see cref="RoutingGateStore"/>;
/// while the router isn't usable that one item is greyed out and a greyed status line names the reason,
/// but the menu itself always opens - see <see cref="ShowTrayMenu"/>.
/// </summary>
/// <remarks>
/// Excluded from code coverage: every method here operates on a live native HWND via P/Invoke
/// (Shell_NotifyIcon, SetWindowLongPtr, TrackPopupMenu, ...) and has no seam to fake a window handle
/// without an actual OS window, so there is nothing a unit test could exercise in-process.
/// </remarks>
[ExcludeFromCodeCoverage]
internal static class TrayWindowManager
{
    private const uint WM_TRAYICON = 0x8000 + 1; // WM_APP + 1
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE = 0xF020;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private const int GWLP_WNDPROC = -4;
    private const int DWMWA_CLOAK = 13;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    private const uint NIM_ADD = 0x0000;
    private const uint NIM_MODIFY = 0x0001;
    private const uint NIM_DELETE = 0x0002;
    private const uint NIM_SETVERSION = 0x0004;
    private const uint NIF_MESSAGE = 0x0001;
    private const uint NIF_ICON = 0x0002;
    private const uint NIF_TIP = 0x0004;
    private const uint NIF_INFO = 0x0010;
    private const uint NIIF_WARNING = 0x0002;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const int IDI_APPLICATION = 32512;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;

    private const uint MF_STRING = 0x0000;
    private const uint MF_GRAYED = 0x0001;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_NONOTIFY = 0x0080;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint SPI_GETWORKAREA = 0x0030;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private const uint CMD_SHOW_DASHBOARD = 1;
    private const uint CMD_EXIT = 2;
    private const uint CMD_TOGGLE_ROUTING = 3;

    // Must match ServiceInstall/@Name in TotallyHotArcRouter.Installer/Package.wxs - that is the name the
    // service control manager knows the router by, and the only handle this app has on its real state.
    private const string RouterServiceName = "TotallyHotArcRouter";

    /// <summary>
    /// Signature matching a native Win32 window procedure, used to invoke the previously-installed
    /// WndProc once it has been replaced by <see cref="WindowProc"/> via <see cref="SetWindowLongPtrW"/>.
    /// </summary>
    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static IntPtr _hwnd;
    private static IntPtr _originalWndProc;

    // Rooted so the GC never collects the delegate backing the native function pointer installed below.
    private static WndProc? _wndProcDelegate;
    private static bool _isExiting;

    // The tray icon's HICON when it came from appicon.ico via LoadImageW, which the caller owns and must
    // release; IntPtr.Zero when AddTrayIcon fell back to the shared IDI_APPLICATION stock icon, which is
    // owned by the system and must NOT be destroyed. RemoveTrayIcon keys its DestroyIcon call off this.
    private static IntPtr _trayIconHandle;

    // Resolved once from the MAUI service provider in Attach - TrayWindowManager is a static P/Invoke
    // wrapper with no DI of its own, so this is the one seam it needs into the app's singletons. Null only
    // if resolution somehow fails (e.g. Application.Current not yet set), in which case the tray menu shows
    // the routing toggle enabled rather than greyed - a safe degrade, not a crash.
    private static RoutingGateStore? _routingGateStore;

    // Captured so the background poll thread that raises RoutingGateStore.BecameUnusable can marshal the
    // balloon notification back onto the UI thread, the same way the low-priority re-hide in Attach does.
    private static DispatcherQueue? _dispatcherQueue;

    /// <summary>
    /// Called once from the MAUI Windows lifecycle when the native window is created: cloaks the window
    /// so startup never paints it, installs the WndProc subclass, adds the tray icon, centers the window,
    /// and hides it so the app starts in the tray only.
    /// </summary>
    public static void Attach(Microsoft.UI.Xaml.Window nativeWindow)
    {
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);

        // Cloaked before anything else, because hiding alone cannot win this race: MAUI activates (shows)
        // the window immediately after this callback returns, which undoes the SW_HIDE below and used to
        // flash a frame of the dashboard on screen until the low-priority re-hide at the end of this
        // method caught up. A cloaked window is still created, laid out, and composed - so WinUI and the
        // BlazorWebView initialize exactly as they would visibly - but it is never painted to the screen,
        // the taskbar, or Alt+Tab. See SetCloaked.
        SetCloaked(true);

        _wndProcDelegate = WindowProc;
        _originalWndProc = SetWindowLongPtrW(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));

        ShowWindowNative(_hwnd, SW_HIDE);

        AddTrayIcon();

        _dispatcherQueue = nativeWindow.DispatcherQueue;
        _routingGateStore = ResolveRoutingGateStore();
        if (_routingGateStore is not null)
        {
            _routingGateStore.BecameUnusable += OnRoutingGateBecameUnusable;
        }

        // MAUI activates (shows) the window right after this lifecycle callback, which would undo the
        // immediate hide above - so also queue a low-priority hide to run after that activation. The
        // window is invisible throughout thanks to the cloak, so this settles the real show state before
        // anyone can see it.
        nativeWindow.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            CenterOnWorkArea();
            ShowWindowNative(_hwnd, SW_HIDE);

            // Uncloaked only now, once the window is genuinely hidden - uncloaking while it is still
            // shown would reveal the very frame this avoids. Order here is load-bearing.
            SetCloaked(false);
        });
    }

    /// <summary>
    /// Cloaks or uncloaks the main window through DWM. A cloaked window keeps its full window and
    /// composition lifetime - it is created, sized, and rendered by the compositor, so XAML lays out and
    /// the BlazorWebView initializes normally - while being invisible on screen and absent from the
    /// taskbar and Alt+Tab. That combination is what lets startup let MAUI activate the window without
    /// the user seeing it, which neither <c>SW_HIDE</c> (activation undoes it) nor an off-screen move
    /// (still visible on another monitor, still in the taskbar) achieves.
    /// </summary>
    /// <param name="cloaked"><see langword="true"/> to cloak the window; <see langword="false"/> to reveal it.</param>
    private static void SetCloaked(bool cloaked)
    {
        // A failed HRESULT here only means the window stays visible - the pre-existing flash - so it is
        // deliberately not escalated into a startup failure.
        var value = cloaked ? 1 : 0;
        _ = DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, in value, sizeof(int));
    }

    /// <summary>
    /// Resolves the tray's one collaborator from the MAUI service provider. <see cref="TrayWindowManager"/>
    /// has no DI of its own (it is a static Win32 wrapper attached via a MAUI lifecycle callback, not a
    /// constructed instance), so this is the seam it uses instead - the same pattern would extend to any
    /// future tray feature that needs an app singleton.
    /// </summary>
    private static RoutingGateStore? ResolveRoutingGateStore() =>
        Application.Current?.Handler?.MauiContext?.Services?.GetService(typeof(RoutingGateStore)) as RoutingGateStore;

    /// <summary>
    /// Proactively shows the router-status balloon the moment <see cref="RoutingGateStore"/> detects the
    /// router stopped being usable, marshaled onto the UI thread since the store's poll loop runs on a
    /// background thread.
    /// </summary>
    private static void OnRoutingGateBecameUnusable() =>
        _dispatcherQueue?.TryEnqueue(ShowRouterUnavailableBalloon);

    /// <summary>
    /// The subclassed window procedure installed over the main window: intercepts tray-icon callbacks,
    /// minimize, and close, forwarding anything unhandled to the original WndProc.
    /// </summary>
    private static IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TRAYICON:
                switch ((int)(lParam & 0xFFFF))
                {
                    case WM_LBUTTONDBLCLK:
                        ShowDashboard();
                        return IntPtr.Zero;
                    case WM_RBUTTONUP:
                    case WM_CONTEXTMENU:
                        ShowTrayMenu();
                        return IntPtr.Zero;
                }

                break;

            case WM_SYSCOMMAND when ((long)wParam & 0xFFF0) == SC_MINIMIZE:
                // Minimize hides to the tray instead of going to the taskbar.
                ShowWindowNative(hWnd, SW_HIDE);
                return IntPtr.Zero;

            case WM_CLOSE when !_isExiting:
                // The title bar X hides to the tray; only the tray menu's Exit really closes.
                ShowWindowNative(hWnd, SW_HIDE);
                return IntPtr.Zero;
        }

        return CallWindowProcW(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Restores and brings the main window to the foreground, as invoked from a tray double-click or the
    /// tray menu's "Show Dashboard" command.
    /// </summary>
    private static void ShowDashboard()
    {
        // Idempotent, and the safety net for the one case Attach cannot cover: if its low-priority
        // uncloak never ran, the window would show without ever painting. Cheap enough to just repeat.
        SetCloaked(false);

        ShowWindowNative(_hwnd, SW_RESTORE);
        ShowWindowNative(_hwnd, SW_SHOW);
        SetForegroundWindow(_hwnd);
    }

    /// <summary>
    /// Builds and displays the tray icon's right-click context menu at the cursor position, then acts on
    /// whichever command the user picks. The menu always opens, including when the router isn't usable
    /// (<see cref="RoutingGateStore.IsUsable"/> is <see langword="false"/>): only the routing toggle depends
    /// on a reachable router, so that one item is replaced by a greyed "Routing Unavailable" and a greyed
    /// status line naming the reason, while "Show Dashboard" and "Exit" stay live. This used to suppress the
    /// menu entirely and show a balloon in its place, which left a user whose service was stopped with no way
    /// to reach Exit from the tray at all - exactly when quitting is the thing they most likely want.
    /// Gated on <see cref="RoutingGateStore.IsUsable"/> rather than <see cref="RoutingGateStore.IsReachable"/>:
    /// a router that answers but rejects the call can't drive the toggle either, so the toggle is equally
    /// dead - only the status line's wording differs.
    /// </summary>
    private static void ShowTrayMenu()
    {
        // Required by TrackPopupMenuEx: without foreground status the menu won't dismiss when the user
        // clicks elsewhere (see the Shell_NotifyIcon docs).
        SetForegroundWindow(_hwnd);

        var routerUsable = _routingGateStore?.IsUsable != false;
        var menu = CreatePopupMenu();
        try
        {
            if (!routerUsable)
            {
                // A greyed, uncommandable caption rather than a real item - TrackPopupMenuEx never returns
                // MF_GRAYED entries, so it needs no CMD_ id and can share CMD_SHOW_DASHBOARD's harmlessly.
                // This carries the short form of what the balloon says at length; see BuildRouterStatusLabel.
                AppendMenuW(menu, MF_STRING | MF_GRAYED, (UIntPtr)CMD_SHOW_DASHBOARD, BuildRouterStatusLabel());
                AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            }

            AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_SHOW_DASHBOARD, "Show Dashboard");
            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);

            // A single flipping item rather than two always-present entries: its label and action reflect
            // whatever RoutingGateStore last polled, so there is never a state where the menu offers an
            // action that would be a no-op. With no reachable router to command, neither label would be
            // truthful, so the item states that plainly instead of guessing a direction.
            var routingLabel = routerUsable
                ? (_routingGateStore?.IsEnabled == false ? "Enable Routing" : "Disable Routing")
                : "Routing Unavailable";
            AppendMenuW(menu, routerUsable ? MF_STRING : MF_STRING | MF_GRAYED, (UIntPtr)CMD_TOGGLE_ROUTING, routingLabel);
            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_EXIT, "Exit");

            GetCursorPos(out var cursor);
            var command = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON, cursor.X, cursor.Y, _hwnd, IntPtr.Zero);

            switch ((uint)command)
            {
                case CMD_SHOW_DASHBOARD:
                    ShowDashboard();
                    break;
                case CMD_TOGGLE_ROUTING:
                    ToggleRouting();
                    break;
                case CMD_EXIT:
                    ExitApplication();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>
    /// Flips routing to the opposite of <see cref="RoutingGateStore.IsEnabled"/>'s last-polled value,
    /// fire-and-forget - the tray menu command handler (<see cref="WindowProc"/>) has no async signature to
    /// await one on.
    /// </summary>
    private static void ToggleRouting()
    {
        if (_routingGateStore is null)
        {
            return;
        }

        _ = ToggleRoutingAsync(enable: !_routingGateStore.IsEnabled);
    }

    /// <summary>
    /// Calls <see cref="RoutingGateStore.EnableAsync"/>/<see cref="RoutingGateStore.DisableAsync"/> and lets
    /// the result land in the store's own polled state - there is nothing further to show here on success or
    /// failure: a failure means the router went away between the right-click's reachability check and this
    /// call, and the very next right-click will see <see cref="RoutingGateStore.IsUsable"/> false and grey
    /// this item out with the reason beside it.
    /// </summary>
    private static async Task ToggleRoutingAsync(bool enable)
    {
        try
        {
            if (enable)
            {
                await _routingGateStore!.EnableAsync();
            }
            else
            {
                await _routingGateStore!.DisableAsync();
            }
        }
        catch (RoutingGateAdminException)
        {
        }
    }

    /// <summary>
    /// Shows a native balloon notification anchored to the tray icon, proactively, the moment the router
    /// stops being usable (see <see cref="OnRoutingGateBecameUnusable"/>). Right-clicking the tray icon
    /// deliberately does not raise this - it opens the menu, which carries the same information in its
    /// greyed status line (see <see cref="BuildRouterStatusLabel"/>). Reuses the same
    /// Shell_NotifyIcon plumbing the tray icon itself is built on (NIF_INFO), so it is visible regardless of
    /// whether the dashboard window is open - unlike the in-app <c>ToastService</c>, which only renders
    /// inside the BlazorWebView the window hides almost all the time.
    /// </summary>
    /// <remarks>
    /// The message is derived from two independent facts - the poll's <see cref="RouterConnectionState"/> and
    /// the service control manager's actual verdict - rather than asserting one from the other. This used to
    /// unconditionally claim "The Windows service is stopped." off nothing more than a failed poll, which was
    /// simply false whenever the service was running and the poll failed for some other reason (most often a
    /// management token the service didn't recognize), and sent users hunting for a problem in services.msc
    /// that was never there.
    /// </remarks>
    private static void ShowRouterUnavailableBalloon()
    {
        var serviceState = TryGetServiceStatus();
        var connectionState = _routingGateStore?.ConnectionState ?? RouterConnectionState.Unreachable;

        var message = (serviceState, connectionState) switch
        {
            // The one case the old message was actually right about.
            (ServiceControllerStatus.Stopped, _) => "The Windows service is stopped.",
            (ServiceControllerStatus.StartPending, _) => "The Windows service is still starting.",
            (ServiceControllerStatus.StopPending, _) => "The Windows service is shutting down.",
            (ServiceControllerStatus.Paused, _) => "The Windows service is paused.",

            // Running, but this GUI can't use it - name the real problem instead of blaming the service.
            (ServiceControllerStatus.Running, RouterConnectionState.Rejected) =>
                "The Windows service is running but rejected this app's request. Its management token may not match; restarting the service regenerates it.",
            (ServiceControllerStatus.Running, RouterConnectionState.Unreachable) =>
                "The Windows service is running but is not accepting connections on its management port yet.",
            (ServiceControllerStatus.Running, _) => "The router is temporarily unavailable.",

            // Service state unknown (not installed, or we lack the rights to query it) - report only what
            // the poll actually established, with no claim about the service either way.
            (null, RouterConnectionState.Rejected) =>
                "The router rejected this app's request. Its management token may not match.",
            _ => "The router is not responding.",
        };

        var data = NewIconData();
        data.uFlags = NIF_INFO;
        data.szInfoTitle = "TotallyHot Arc Router";
        data.szInfo = message;
        data.dwInfoFlags = NIIF_WARNING;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>
    /// The short, menu-width form of <see cref="ShowRouterUnavailableBalloon"/>'s message, shown as the tray
    /// menu's greyed status caption while the router isn't usable. Derived from the same two independent
    /// facts for the same reason - the service control manager's verdict and the poll's
    /// <see cref="RouterConnectionState"/> - so the menu never claims the service is stopped on the strength
    /// of a failed poll alone. Kept separate from the balloon's text rather than shared: a balloon has room
    /// for a sentence of remediation, a menu item has room for a few words.
    /// </summary>
    private static string BuildRouterStatusLabel() => (TryGetServiceStatus(), _routingGateStore?.ConnectionState ?? RouterConnectionState.Unreachable) switch
    {
        (ServiceControllerStatus.Stopped, _) => "Router service: stopped",
        (ServiceControllerStatus.StartPending, _) => "Router service: starting",
        (ServiceControllerStatus.StopPending, _) => "Router service: stopping",
        (ServiceControllerStatus.Paused, _) => "Router service: paused",
        (ServiceControllerStatus.Running, RouterConnectionState.Rejected) => "Router: request rejected",
        (ServiceControllerStatus.Running, _) => "Router: not responding yet",
        (null, RouterConnectionState.Rejected) => "Router: request rejected",
        _ => "Router: not responding",
    };

    /// <summary>
    /// Reads the installed router service's current status, or <see langword="null"/> when it can't be
    /// determined - the service isn't installed (a developer running the router from the command line), or
    /// this process lacks the rights to query it. Null is deliberately distinct from
    /// <see cref="ServiceControllerStatus.Stopped"/>: "I couldn't check" must never render as "it is
    /// stopped", which is the entire class of bug this method exists to end.
    /// </summary>
    private static ServiceControllerStatus? TryGetServiceStatus()
    {
        try
        {
            using var controller = new ServiceController(RouterServiceName);
            return controller.Status;
        }
        catch (InvalidOperationException)
        {
            // Thrown (wrapping a Win32Exception) when the service does not exist or can't be opened.
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Removes the tray icon and quits the application, marking <see cref="_isExiting"/> first so the
    /// resulting WM_CLOSE is allowed to pass through the WndProc subclass instead of being redirected to
    /// hide the window.
    /// </summary>
    private static void ExitApplication()
    {
        _isExiting = true;
        RemoveTrayIcon();

        // WndProc callbacks run on the UI thread, so Quit() can be called directly. The WM_CLOSE this
        // triggers passes through the subclass because _isExiting is now set.
        Application.Current?.Quit();
    }

    /// <summary>
    /// Creates and registers the notify-icon for the main window, then opts it into the modern
    /// notify-icon version for consistent click/tooltip behavior.
    /// </summary>
    private static void AddTrayIcon()
    {
        var data = NewIconData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_TRAYICON;
        data.hIcon = LoadAppIcon();
        data.szTip = "TotallyHot Arc Router";
        Shell_NotifyIconW(NIM_ADD, ref data);

        // Opt into the modern notify-icon behavior (consistent callback semantics for double-click vs.
        // click-and-hold, tooltips, etc. across Windows versions). Must be sent after NIM_ADD.
        data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref data);
    }

    /// <summary>
    /// Unregisters the tray icon for the main window.
    /// </summary>
    private static void RemoveTrayIcon()
    {
        var data = NewIconData();
        Shell_NotifyIconW(NIM_DELETE, ref data);

        // Only ours to destroy - see _trayIconHandle. Released after NIM_DELETE so the shell is no longer
        // referencing the icon when the handle goes away.
        if (_trayIconHandle != IntPtr.Zero)
        {
            DestroyIcon(_trayIconHandle);
            _trayIconHandle = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Loads the application's own icon for the tray, falling back to the Windows stock application icon
    /// if it cannot be loaded. The icon is <c>appicon.ico</c> beside the executable - the file Resizetizer
    /// generates from <c>Resources/AppIcon/appicon.svg</c> (the csproj's <c>MauiIcon</c> item) and copies
    /// to the output directory, so the tray, the taskbar, and the executable all show one icon from one
    /// source rather than the tray showing a generic placeholder.
    /// </summary>
    /// <remarks>
    /// Loaded at the system's small-icon metrics rather than <c>LR_DEFAULTSIZE</c> so the result matches
    /// what the notification area asks for at the current DPI. Resizetizer currently emits a single 64x64
    /// frame, so this is a downscale rather than a frame selection; it becomes a frame selection for free
    /// if the icon ever gains 16x16/32x32 frames, which is the reason to request the real size here rather
    /// than hardcode 16. A missing or unreadable file is a cosmetic problem, not a reason to fail startup,
    /// so the stock icon is used instead - that is also the pre-existing behavior this replaced.
    /// </remarks>
    private static IntPtr LoadAppIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "appicon.ico");
        if (File.Exists(iconPath))
        {
            var handle = LoadImageW(
                IntPtr.Zero,
                iconPath,
                IMAGE_ICON,
                GetSystemMetrics(SM_CXSMICON),
                GetSystemMetrics(SM_CYSMICON),
                LR_LOADFROMFILE);

            if (handle != IntPtr.Zero)
            {
                _trayIconHandle = handle;
                return handle;
            }
        }

        _trayIconHandle = IntPtr.Zero;
        return LoadIconW(IntPtr.Zero, (IntPtr)IDI_APPLICATION);
    }

    /// <summary>
    /// Builds a <see cref="NOTIFYICONDATAW"/> populated with the fields common to every
    /// Shell_NotifyIcon call for the main window's tray icon.
    /// </summary>
    private static NOTIFYICONDATAW NewIconData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    /// <summary>
    /// Repositions the main window so it is centered within the current monitor's work area.
    /// </summary>
    private static void CenterOnWorkArea()
    {
        var workArea = default(RECT);
        if (!SystemParametersInfoW(SPI_GETWORKAREA, 0, ref workArea, 0) || !GetWindowRect(_hwnd, out var window))
        {
            return;
        }

        var width = window.Right - window.Left;
        var height = window.Bottom - window.Top;
        var x = workArea.Left + ((workArea.Right - workArea.Left - width) / 2);
        var y = workArea.Top + ((workArea.Bottom - workArea.Top - height) / 2);
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Managed layout of the Win32 POINT structure, used to receive the cursor position from
    /// <see cref="GetCursorPos"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// Managed layout of the Win32 RECT structure, used for the window and work-area rectangles returned
    /// by <see cref="GetWindowRect"/> and <see cref="SystemParametersInfoW"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// Managed layout of the Win32 NOTIFYICONDATAW structure, used to describe the tray icon passed to
    /// <see cref="Shell_NotifyIconW"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    /// <summary>
    /// P/Invoke binding for the Win32 SetWindowLongPtrW API, used to install the WndProc subclass on the
    /// main window and to read back the original window procedure pointer.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>
    /// P/Invoke binding for the Win32 CallWindowProcW API, used to forward unhandled messages from the
    /// subclassed WndProc to the original window procedure.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// P/Invoke binding for the Win32 Shell_NotifyIconW API, used to add, update, and remove the tray
    /// icon.
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    /// <summary>
    /// P/Invoke binding for the Win32 LoadIconW API, used to load the stock application icon shown in the
    /// tray when the application's own icon file cannot be loaded.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    /// <summary>
    /// P/Invoke binding for the Win32 LoadImageW API, used to load the application's own icon from
    /// <c>appicon.ico</c> at the system's small-icon size for the tray.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(
        IntPtr hInst,
        string name,
        uint type,
        int cx,
        int cy,
        uint fuLoad);

    /// <summary>
    /// P/Invoke binding for the Win32 DestroyIcon API, used to release the icon handle
    /// <see cref="LoadAppIcon"/> loaded once the tray icon has been removed.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// P/Invoke binding for the Win32 GetSystemMetrics API, used to ask for the notification area's
    /// small-icon dimensions so the correct frame is taken from the multi-resolution icon file.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>
    /// P/Invoke binding for the Win32 ShowWindow API, used to hide, show, and restore the main window.
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    private static extern bool ShowWindowNative(IntPtr hWnd, int nCmdShow);

    /// <summary>
    /// P/Invoke binding for the Win32 SetForegroundWindow API, used to bring the main window or the tray
    /// menu to the foreground.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// P/Invoke binding for the DWM DwmSetWindowAttribute API, used by <see cref="SetCloaked"/> to hide
    /// the main window from the compositor's output during startup.
    /// </summary>
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, in int pvAttribute, int cbAttribute);

    /// <summary>
    /// P/Invoke binding for the Win32 CreatePopupMenu API, used to create the tray icon's context menu.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    /// <summary>
    /// P/Invoke binding for the Win32 AppendMenuW API, used to add items and separators to the tray
    /// context menu.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    /// <summary>
    /// P/Invoke binding for the Win32 DestroyMenu API, used to free the tray context menu after it is
    /// dismissed.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    /// <summary>
    /// P/Invoke binding for the Win32 TrackPopupMenuEx API, used to display the tray context menu and
    /// block until the user picks a command or dismisses it.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    /// <summary>
    /// P/Invoke binding for the Win32 GetCursorPos API, used to position the tray context menu at the
    /// cursor.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>
    /// P/Invoke binding for the Win32 GetWindowRect API, used to read the main window's current bounds
    /// when centering it.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>
    /// P/Invoke binding for the Win32 SetWindowPos API, used to reposition the main window when centering
    /// it on the work area.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    /// <summary>
    /// P/Invoke binding for the Win32 SystemParametersInfoW API, used to read the current monitor's work
    /// area when centering the main window.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);
}

