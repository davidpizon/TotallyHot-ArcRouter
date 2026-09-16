using System.Diagnostics.CodeAnalysis;

namespace TotallyHot.ArcRouter.Tray;

/// <summary>
/// Entry point for the Windows tray shell (web GUI migration plan Phase P8). Excluded from coverage like
/// every other thin platform shell in this codebase (<c>TotallyHotArcRouter.Gui</c>'s
/// <c>Platforms/Windows/TrayWindowManager.cs</c>) - the logic worth testing lives in
/// <c>TotallyHotArcRouter.Tray.Core</c>, exercised by <c>TotallyHotArcRouter.Tray.Core.Tests</c>.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class Program
{
    /// <summary>
    /// A session-local (not machine-wide - a per-user tray icon makes sense per logged-on session, e.g.
    /// Remote Desktop) mutex enforcing a single running instance, mirroring the reasoning
    /// <c>TrayWindowManager</c>'s remarks give for why a second tray would just be confusing (two icons,
    /// two pollers, no crash either way) rather than harmful.
    /// </summary>
    private const string SingleInstanceMutexName = "Local\\TotallyHotArcRouter.Tray";

    [STAThread]
    private static void Main()
    {
        using var singleInstanceMutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName,
            createdNew: out var createdNew);
        if (!createdNew) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
