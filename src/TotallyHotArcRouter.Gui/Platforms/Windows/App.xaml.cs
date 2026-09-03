using Serilog;
using System.Diagnostics.CodeAnalysis;
using UnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;

namespace TotallyHot.ArcRouter.Gui.WinUI;

/// <summary>
/// WinUI bootstrap for the MAUI app. The XAML compiler also generates the process entry point from the
/// companion App.xaml, which is why this class must remain XAML-backed.
/// </summary>
/// <remarks>
/// Excluded from code coverage: XAML-backed and only runs as part of process startup via the compiler
/// generated entry point; <see cref="MauiProgram.CreateMauiApp"/> is separately excluded for the same
/// live-host reason.
/// </remarks>
[ExcludeFromCodeCoverage]
public partial class App : MauiWinUIApplication
{
    /// <summary>
    /// Initializes the XAML-generated component and subscribes the WinUI unhandled-exception hook. That
    /// hook is separate from the app-domain one <see cref="MauiProgram.CreateMauiApp"/> installs and
    /// catches a different population: an exception escaping a XAML or UI-thread callback is handled by
    /// WinUI itself, so it never reaches the app domain and - before this - ended the GUI with nothing
    /// written anywhere.
    /// </summary>
    public App()
    {
        InitializeComponent();

        UnhandledException += OnUnhandledException;
    }

    /// <inheritdoc/>
    protected override MauiApp CreateMauiApp()
    {
        return MauiProgram.CreateMauiApp();
    }

    /// <summary>
    /// Logs an exception WinUI caught on the UI thread and flushes immediately, since the process is
    /// about to end. <c>e.Handled</c> is deliberately left <see langword="false"/>: this hook exists to
    /// make the crash visible, not to swallow it and leave the app running in an unknown state.
    /// </summary>
    /// <param name="sender">The WinUI application raising the event; unused.</param>
    /// <param name="e">Carries the exception and its message.</param>
    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Fatal(exception: e.Exception,
            messageTemplate: "Unhandled exception on the WinUI thread: {ExceptionMessage}", propertyValue: e.Message);
        Log.CloseAndFlush();
    }
}