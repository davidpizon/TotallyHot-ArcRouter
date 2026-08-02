using System.Diagnostics.CodeAnalysis;

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
    /// <summary>Initializes the XAML-generated component.</summary>
    public App()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}

