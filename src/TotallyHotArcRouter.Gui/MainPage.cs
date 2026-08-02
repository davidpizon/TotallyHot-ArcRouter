using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace TotallyHot.ArcRouter.Gui;

/// <summary>
/// The app's single page: a BlazorWebView filling the window, hosting the Razor dashboard rooted at
/// <see cref="Components.Dashboard"/>.
/// </summary>
/// <remarks>
/// Excluded from code coverage: this constructor only wires a <see cref="BlazorWebView"/> to a root
/// component and has no branching logic; the Razor components it hosts are unit-tested independently
/// (see TotallyHot.ArcRouter.Gui.Tests).
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class MainPage : ContentPage
{
    /// <summary>Wires the BlazorWebView to <see cref="Components.Dashboard"/> as its root component.</summary>
    public MainPage()
    {
        Content = new BlazorWebView
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
    }
}

