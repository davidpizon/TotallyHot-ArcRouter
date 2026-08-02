using TotallyHot.ArcRouter.Sandbox.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Sandbox.Firecracker;

/// <summary>
/// Warm pool for Tier-2 microVMs. Concurrency is bounded to <see cref="WarmPoolOptions.Tier2"/>; each lease
/// gets a fresh per-run working directory (API socket + Copy-on-Write overlay), discarded on return — the
/// snapshot itself is restored per run by the launcher, so "reset" is discarding the overlay (§4.2). The
/// directory bookkeeping is OS-agnostic and unit-testable.
/// </summary>
public sealed class FirecrackerSnapshotPool : ISandboxPool, IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly string _baseDirectory;
    private readonly ILogger<FirecrackerSnapshotPool> _logger;

    /// <summary>Initializes a new instance of the <see cref="FirecrackerSnapshotPool"/> class.</summary>
    /// <param name="options">The sandbox options (warm-pool sizing, runtime directory).</param>
    /// <param name="logger">The logger.</param>
    public FirecrackerSnapshotPool(IOptions<SandboxOptions> options, ILogger<FirecrackerSnapshotPool> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var value = options.Value;
        var size = Math.Max(1, value.WarmPool.Tier2);
        _slots = new SemaphoreSlim(size, size);
        _logger = logger;
        _baseDirectory = value.Firecracker.RuntimeDirectory is { Length: > 0 } dir
            ? dir
            : Path.Combine(Path.GetTempPath(), "acrouter-microvm");
        Directory.CreateDirectory(_baseDirectory);
    }

    /// <inheritdoc />
    public SandboxTier Tier => SandboxTier.Tier2MicroVm;

    /// <inheritdoc />
    public async ValueTask<ISandboxLease> LeaseAsync(CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.Combine(_baseDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return new Lease(directory, Release);
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _slots.Dispose();

    /// <summary>Deletes the per-run working directory (the overlay), discarding it as the run's "reset", and releases the pool slot regardless of outcome.</summary>
    private ValueTask Release(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Failed to reset microVM working directory {Directory}.", directory);
        }
        finally
        {
            _slots.Release();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>A leased microVM working directory, returned to the pool for cleanup and slot release when disposed.</summary>
    /// <param name="workingDirectory">The per-run working directory holding the API socket and Copy-on-Write overlay.</param>
    /// <param name="release">The callback invoked on disposal to discard the working directory and free the pool slot.</param>
    private sealed class Lease(string workingDirectory, Func<string, ValueTask> release) : ISandboxLease
    {
        /// <inheritdoc />
        public SandboxTier Tier => SandboxTier.Tier2MicroVm;

        /// <inheritdoc />
        public string WorkingDirectory => workingDirectory;

        /// <inheritdoc />
        public ValueTask DisposeAsync() => release(workingDirectory);
    }
}

