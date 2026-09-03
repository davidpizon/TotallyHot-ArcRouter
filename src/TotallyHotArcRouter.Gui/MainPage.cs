using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Serilog;
using System.Diagnostics.CodeAnalysis;
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
                    ComponentType = typeof(Components.Dashboard),
                },
            },
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
    private static void OnBlazorWebViewInitializing(object? sender, BlazorWebViewInitializingEventArgs e) =>
        Log.Information(
            "BlazorWebView initializing with WebView2 user-data folder {UserDataFolder}.",
            string.IsNullOrWhiteSpace(e.UserDataFolder) ? "<WebView2 default>" : e.UserDataFolder);

    /// <summary>
    /// Logs that the WebView2 control came up, with the runtime version that answered. The absence of
    /// this line after an "initializing" line is itself the diagnosis for a blank dashboard.
    /// </summary>
    /// <param name="sender">The BlazorWebView raising the event; unused.</param>
    /// <param name="e">Carries the initialized platform WebView2 control.</param>
    private static void OnBlazorWebViewInitialized(object? sender, BlazorWebViewInitializedEventArgs e) =>
        Log.Information(
            "BlazorWebView initialized. WebView2 runtime version {BrowserVersion}.",
            e.WebView.CoreWebView2?.Environment.BrowserVersionString ?? "unknown");

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
            // Lambdas rather than method groups: these event argument types are WinRT projections whose
            // assembly this project only references transitively, so letting the compiler infer them from
            // the delegates keeps those types out of this file's signatures.
            platformView.CoreWebView2Initialized += (_, _) =>
                Log.Debug("The platform WebView2 control obtained its CoreWebView2.");

            platformView.CoreProcessFailed += (_, args) => Log.Error(
                "The WebView2 browser process failed ({ProcessFailedKind}); the dashboard will stop rendering.",
                args.ProcessFailedKind);
        }
    }
}
