namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Copies text to the system clipboard. Introduced (web GUI migration plan Phase P5a) so
/// <c>Components/ConsoleTab.razor</c> - which moves into the browser-targeted
/// <c>TotallyHot.ArcRouter.Gui.Components</c> library in Phase P5b - never calls MAUI's native
/// <c>Clipboard.Default</c> directly: that API doesn't exist in a browser. The native (MAUI) and future
/// WASM (<c>navigator.clipboard</c> via JS interop) hosts each register their own implementation.
/// </summary>
public interface IClipboardService
{
    /// <summary>Sets the clipboard's text content, replacing whatever was there.</summary>
    /// <param name="text">The text to copy.</param>
    Task SetTextAsync(string text);
}
