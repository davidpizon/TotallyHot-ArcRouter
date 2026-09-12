using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.UI.Dispatching;
using Serilog;
using System.Diagnostics.CodeAnalysis;
using TotallyHot.ArcRouter.Gui.Components;
using WebView2Control = Microsoft.UI.Xaml.Controls.WebView2;

namespace TotallyHot.ArcRouter.Gui;

/// <summary>
/// The app's single page: a BlazorWebView filling the window, hosting the Razor dashboard rooted at
/// <see cref="Components.Dashboard"/>, with its initialization traced to the GUI log.
/// </summary>
/// <remarks>
/// Excluded from code coverage: this constructor only wires a <see cref="BlazorWebView"/> to a root
/// component and subscribes log handlers to its events, and the handlers themselves need a live WebView2
/// to fire; the Razor components it hosts are unit-tested independently (see TotallyHot.ArcRouter.Gui.Tests).
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class MainPage : ContentPage
{
    // Rooted here (rather than only local to the constructor) so TrayWindowManager - a static Win32
    // wrapper with no view of the BlazorWebView it shows and hides - can reach the platform control
    // through SetWebViewVisible. Null until OnHandlerChanged fires.
    private static WebView2Control? _platformWebView;

    /// <summary>
    /// Wires the BlazorWebView to <see cref="Components.Dashboard"/> as its root component and traces
    /// its initialization. The tracing exists because the failure mode this page has actually shown in
    /// the field is silent: when WebView2 cannot create its environment the control simply never gets a
    /// CoreWebView2, so the window opens blank with no exception and no crash. A log that records the
    /// attempt, the folder it was made against, and either the browser version that answered or the
    /// exception that did not, is the difference between diagnosing that and guessing.
    /// </summary>
    public MainPage()
    {
        var webView = new BlazorWebView
        {
            HostPage = "wwwroot/index.html",
            RootComponents =
            {
                new RootComponent
                {
                    Selector = "#root",
                    ComponentType = typeof(Dashboard)
                }
            }
        };

        webView.BlazorWebViewInitializing += OnBlazorWebViewInitializing;
        webView.BlazorWebViewInitialized += OnBlazorWebViewInitialized;
        webView.HandlerChanged += OnHandlerChanged;

        Content = webView;
    }

    /// <summary>
    /// Logs the WebView2 environment about to be created, including the user-data folder it will use.
    /// A blank value here means MAUI is falling back to <c>&lt;exe&gt;.WebView2</c> beside the
    /// executable, which is unwritable in the installed layout - see
    /// <see cref="Services.WebViewUserData"/>.
    /// </summary>
    /// <param name="sender">The BlazorWebView raising the event; unused.</param>
    /// <param name="e">Carries the WebView2 environment options and user-data folder MAUI will use.</param>
    private static void OnBlazorWebViewInitializing(object? sender, BlazorWebViewInitializingEventArgs e)
    {
        Log.Information(
            messageTemplate: "BlazorWebView initializing with WebView2 user-data folder {UserDataFolder}.",
            propertyValue: string.IsNullOrWhiteSpace(e.UserDataFolder) ? "<WebView2 default>" : e.UserDataFolder);
    }

    /// <summary>
    /// Logs that the WebView2 control came up, with the runtime version that answered. The absence of
    /// this line after an "initializing" line is itself the diagnosis for a blank dashboard.
    /// </summary>
    /// <param name="sender">The BlazorWebView raising the event; unused.</param>
    /// <param name="e">Carries the initialized platform WebView2 control.</param>
    private static void OnBlazorWebViewInitialized(object? sender, BlazorWebViewInitializedEventArgs e)
    {
        Log.Information(
            messageTemplate: "BlazorWebView initialized. WebView2 runtime version {BrowserVersion}.",
            propertyValue: e.WebView.CoreWebView2?.Environment.BrowserVersionString ?? "unknown");
    }

    /// <summary>
    /// Subscribes to the platform control's process-failure event as soon as MAUI creates its handler.
    /// This covers the half of a blank dashboard that nothing else reports: WinUI's WebView2 has no
    /// "initialization failed" event (unlike the WinForms and WPF controls), so a CoreWebView2 that never
    /// materializes is only visible as .NET MAUI's own <c>FailedToCreateWebView2Environment</c> log entry
    /// - which reaches the file because <c>MauiProgram</c> routes Microsoft.Extensions.Logging into
    /// Serilog - while a browser process that dies *after* a successful start is only visible here.
    /// </summary>
    /// <param name="sender">The BlazorWebView whose handler changed.</param>
    /// <param name="e">Empty event arguments; unused.</param>
    private static void OnHandlerChanged(object? sender, EventArgs e)
    {
        if (sender is BlazorWebView { Handler.PlatformView: WebView2Control platformView })
        {
            _platformWebView = platformView;

            // Lambdas rather than method groups: these event argument types are WinRT projections whose
            // assembly this project only references transitively, so letting the compiler infer them from
            // the delegates keeps those types out of this file's signatures.
            platformView.CoreWebView2Initialized += (_, _) =>
                Log.Debug("The platform WebView2 control obtained its CoreWebView2.");

            platformView.CoreProcessFailed += (_, args) => Log.Error(
                messageTemplate:
                "The WebView2 browser process failed ({ProcessFailedKind}); the dashboard will stop rendering.",
                propertyValue: args.ProcessFailedKind);
        }
    }

    /// <summary>
    /// Keeps the platform WebView2 control's XAML <c>Visibility</c> in step with
    /// <see cref="Platforms.Windows.TrayWindowManager"/> hiding and showing the main window, forcing a
    /// fresh layout/composition pass on the way back to visible.
    /// </summary>
    /// <remarks>
    /// This control hosts CoreWebView2 as a DirectComposition visual rather than as a classic child HWND,
    /// and exposes no public hook to tell it a hosted window's visibility changed. WinUI/XAML's own
    /// composition wiring is keyed off the element's own <c>Visibility</c>, not off the
    /// host window's actual on-screen state - so <see cref="Platforms.Windows.TrayWindowManager"/> hiding,
    /// DWM-cloaking, and showing the main window entirely through raw Win32/DWM calls never touches that
    /// wiring, and nothing ever asks the composited surface to re-establish itself once the window is
    /// shown again. The browser process itself keeps running throughout (hence the clean "BlazorWebView
    /// initialized" log line with no error after it), but the composited frame it is drawing never gets
    /// reattached, leaving the dashboard a flat, empty background behind otherwise-correct native chrome.
    /// Collapsing the element and restoring it on the next dispatcher tick - rather than only ever setting
    /// <see cref="Microsoft.UI.Xaml.Visibility.Visible"/> - is what forces WinUI to redo that attachment instead of treating
    /// an already-<see cref="Microsoft.UI.Xaml.Visibility.Visible"/> element as unchanged and skipping it.
    /// A missing platform control (no call has reached <see cref="OnHandlerChanged"/> yet) is logged and
    /// otherwise ignored rather than throwing, since the very first hide can happen before the
    /// BlazorWebView has finished initializing.
    /// </remarks>
    /// <param name="visible">
    /// <see langword="true"/> to force a re-composition pass so the dashboard resumes painting;
    /// <see langword="false"/> to collapse the control while the window is hidden.
    /// </param>
    internal static void SetWebViewVisible(bool visible)
    {
        if (_platformWebView is not { } webView)
        {
            Log.Debug(
                messageTemplate: "SetWebViewVisible({Visible}) skipped: no platform WebView2 control yet.",
                propertyValue: visible);
            return;
        }

        if (!visible)
        {
            webView.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            Log.Debug("WebView2 control collapsed for the tray hide.");
            return;
        }

        webView.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
        {
            webView.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            Log.Debug("WebView2 control restored to Visible after a forced layout pass.");
        });
    }
}