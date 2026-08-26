using System.Diagnostics;

namespace TotallyHot.ArcRouter.Updater;

/// <summary>Production <see cref="IProcessWaiter"/>, wrapping <see cref="Process.GetProcessById(int)"/> and <see cref="Process.WaitForExitAsync"/>.</summary>
public sealed class ProcessWaiter : IProcessWaiter
{
    /// <inheritdoc />
    public async Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // No such process - already exited (or never existed). Trivially satisfied.
            return true;
        }

        using (process)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The linked token fired because CancelAfter's timeout elapsed, not because the caller
                // cancelled - report the timeout as "still running" rather than propagating.
                return false;
            }
        }
    }
}
