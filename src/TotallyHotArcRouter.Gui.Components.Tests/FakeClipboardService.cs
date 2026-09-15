using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>An in-memory <see cref="IClipboardService"/> for tests - never touches a real system clipboard.</summary>
public sealed class FakeClipboardService : IClipboardService
{
    /// <summary>The text from the most recent <see cref="SetTextAsync"/> call, or <see langword="null"/> if none yet.</summary>
    public string? LastCopiedText { get; private set; }

    /// <inheritdoc/>
    public Task SetTextAsync(string text)
    {
        LastCopiedText = text;
        return Task.CompletedTask;
    }
}
