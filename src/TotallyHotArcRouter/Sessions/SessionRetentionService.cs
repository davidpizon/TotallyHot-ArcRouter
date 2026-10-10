using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// Enforces ADR-0019's retention rule on session storage: keep the newest N turns, where N is the Sample Size
/// setting (<see cref="RoutingOptions.EmbeddingMemoryCapacity"/>, read live so a change applies on the next
/// pass), by deleting whole oldest sessions. It runs whether or not capture is currently switched on, because
/// turning capture off must not strand the sessions already written; <see cref="SessionMaintenance"/> makes it
/// a no-op until something has been captured. It only deletes; the cost of one pass is a read of the index,
/// plus a key rotation when a session was removed.
/// </summary>
/// <param name="maintenance">Deletes sessions, or does nothing when none was ever captured.</param>
/// <param name="routingOptions">Supplies the live Sample Size.</param>
/// <param name="logger">Receives failures; message templates are static.</param>
public sealed class SessionRetentionService(
    SessionMaintenance maintenance,
    IOptionsMonitor<RoutingOptions> routingOptions,
    ILogger<SessionRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Leave the thread that started the host; a pass does synchronous SQLite and file work.
        await Task.Yield();

        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            do
            {
                RunOnce();
            } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Runs one retention pass - the loop body <see cref="ExecuteAsync"/> runs on every tick. A failure is
    /// logged and the next tick tries again. Internal so tests can run a pass without waiting out the timer.
    /// </summary>
    /// <returns>What the pass deleted, or <see langword="null"/> when it failed.</returns>
    internal SessionDeletionResult? RunOnce()
    {
        try
        {
            var result = maintenance.EnforceRetention(routingOptions.CurrentValue.EmbeddingMemoryCapacity);
            if (result.DeletedSessions > 0)
            {
                logger.LogInformation(
                    "Session retention deleted {Count} sessions to stay within the Sample Size.", result.DeletedSessions);
            }

            if (result.DeletedNullArchiveRows > 0)
            {
                logger.LogInformation(
                    "Retention deleted {Count} transcript rows with no archive session id.",
                    result.DeletedNullArchiveRows);
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Session retention pass failed; it will be retried.");
            return null;
        }
    }
}
