namespace TotallyHot.ArcRouter.Updater;

/// <summary>
/// Waits for the calling Router process to exit before <see cref="UpdaterService"/> touches any files -
/// a running process's own files cannot be overwritten, which is the entire reason this helper exists.
/// An abstraction over <see cref="System.Diagnostics.Process"/> so unit tests never need to spawn and
/// kill a real process to exercise the wait.
/// </summary>
public interface IProcessWaiter
{
    /// <summary>
    /// Waits until the process named by <paramref name="processId"/> exits, or returns
    /// <see langword="true"/> immediately if it has already exited or never existed (an already-dead
    /// caller is not a failure - it just means the wait is trivially satisfied). Returns
    /// <see langword="false"/> if the process is still running after <paramref name="timeout"/>.
    /// </summary>
    Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken);
}
