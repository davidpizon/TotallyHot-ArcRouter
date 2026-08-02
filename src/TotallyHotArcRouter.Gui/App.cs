using System.Diagnostics.CodeAnalysis;

namespace TotallyHot.ArcRouter.Gui;

/// <summary>
/// MAUI application root. Creates the single dashboard window; tray behavior (launch hidden, hide on
/// minimize/close, tray context menu) is wired natively in
/// <see cref="Platforms.Windows.TrayWindowManager"/> once the native window exists.
/// </summary>
/// <remarks>
/// Excluded from code coverage: <see cref="CreateWindow"/> only runs inside a live MAUI application
/// host and just wires together already-tested pieces (<see cref="MainPage"/>, window chrome), so there
/// is no independent behavior here for a unit test to verify in-process.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class App : Application
{
    /// <summary>Creates the single dashboard window hosting <see cref="MainPage"/>.</summary>
    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage())
        {
            Title = "TotallyHot Arc Router Dashboard",
            Width = 1440,
            Height = 900,
        };
}

