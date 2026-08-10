namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Deletes a temp file when disposed. Registered as a singleton in a bUnit
/// <c>BunitContext</c> so a per-test temp settings file (e.g. for <c>GuiSettingsStore</c>)
/// is removed automatically when the context's service provider is disposed.
/// </summary>
/// <param name="path">The temp file path to delete on dispose.</param>
internal sealed class TempFileCleanup(string path) : IDisposable
{
    /// <summary>Deletes the file at <see cref="path"/> if it still exists.</summary>
    public void Dispose()
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
