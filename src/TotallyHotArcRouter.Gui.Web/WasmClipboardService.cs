using Microsoft.JSInterop;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Web;

/// <summary>
/// The browser <see cref="IClipboardService"/> implementation (web GUI migration plan Phase P6):
/// <c>navigator.clipboard.writeText</c> via JS interop (<c>wwwroot/js/clipboard-interop.js</c>), the
/// browser counterpart to <c>TotallyHotArcRouter.Gui</c>'s <c>MauiClipboardService</c>.
/// </summary>
public sealed class WasmClipboardService : IClipboardService
{
    private readonly IJSRuntime _jsRuntime;

    /// <summary>Initializes a new instance of the <see cref="WasmClipboardService"/> class.</summary>
    /// <param name="jsRuntime">Used to invoke the browser's Clipboard API.</param>
    public WasmClipboardService(IJSRuntime jsRuntime)
    {
        ArgumentNullException.ThrowIfNull(jsRuntime);
        _jsRuntime = jsRuntime;
    }

    /// <inheritdoc/>
    public async Task SetTextAsync(string text)
    {
        await _jsRuntime.InvokeVoidAsync(identifier: "clipboardInterop.writeText", args: text).ConfigureAwait(false);
    }
}
