using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui;

/// <summary>
/// The native <see cref="IClipboardService"/> implementation: MAUI's <see cref="Clipboard.Default"/>.
/// Deliberately kept out of <c>Services/</c> (unlike <see cref="IClipboardService"/> itself) so it does not
/// get swept into the browser-targeted <c>TotallyHot.ArcRouter.Gui.Components</c> library in Phase P5b -
/// this class stays in the MAUI host, registered once in <c>MauiProgram</c>.
/// </summary>
public sealed class MauiClipboardService : IClipboardService
{
    /// <inheritdoc/>
    public Task SetTextAsync(string text)
    {
        return Clipboard.Default.SetTextAsync(text);
    }
}
