// Backs WasmClipboardService - the browser IClipboardService implementation (web GUI migration plan
// Phase P6). navigator.clipboard.writeText is itself promise-based, so this is a thin, direct wrapper;
// it exists at all only because Blazor JS interop calls into a named global function, not an arbitrary
// expression.
window.clipboardInterop = {
    writeText: function (text) {
        return navigator.clipboard.writeText(text);
    }
};
