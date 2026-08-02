using TotallyHot.ArcRouter.Sandbox.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Sandbox.Tier1;

/// <summary>
/// Warm pool for Tier-1 jails. Concurrency is bounded to <see cref="WarmPoolOptions.Tier1"/> by a
/// semaphore; each lease gets a fresh working directory (on <c>/dev/shm</c> where available, so it lives in
/// RAM), and disposal deletes it — the doc's "discard the per-lease tmpfs" reset (§4.2). The directory
/// creation/deletion is OS-agnostic, so the pool is unit-testable off Linux.
/// </summary>
public sealed class NativeJailPool : ISandboxPool, IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly string _baseDirectory;
    private readonly ILogger<NativeJailPool> _logger;

    /// <summary>Initializes a new instance of the <see cref="NativeJailPool"/> class.</summary>
    /// <param name="options">The sandbox options (warm-pool sizing).</param>
    /// <param name="logger">The logger.</param>
    public NativeJailPool(IOptions<SandboxOptions> options, ILogger<NativeJailPool> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var size = Math.Max(1, options.Value.WarmPool.Tier1);
        _slots = new SemaphoreSlim(size, size);
        _logger = logger;
        _baseDirectory = ResolveBaseDirectory();
        Directory.CreateDirectory(_baseDirectory);
    }

    /// <inheritdoc />
    public SandboxTier Tier => SandboxTier.Tier1Jail;

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

    /// <summary>Deletes the per-lease working directory (the tmpfs reset) and releases the pool slot regardless of outcome.</summary>
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
            _logger.LogDebug(ex, "Failed to reset jail working directory {Directory}.", directory);
        }
        finally
        {
            _slots.Release();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Chooses the base directory for jail working directories, preferring RAM-backed <c>/dev/shm</c> on Linux and falling back to the OS temp directory elsewhere.</summary>
    private static string ResolveBaseDirectory()
    {
        // Prefer /dev/shm (tmpfs, RAM-backed) on Linux; fall back to the OS temp directory elsewhere.
        if (OperatingSystem.IsLinux() && Directory.Exists("/dev/shm"))
        {
            return Path.Combine("/dev/shm", "acrouter-jail");
        }

        return Path.Combine(Path.GetTempPath(), "acrouter-jail");
    }

    /// <summary>A leased jail working directory, returned to the pool for cleanup and slot release when disposed.</summary>
    /// <param name="workingDirectory">The per-lease working directory, typically backed by tmpfs.</param>
    /// <param name="release">The callback invoked on disposal to discard the working directory and free the pool slot.</param>
    private sealed class Lease(string workingDirectory, Func<string, ValueTask> release) : ISandboxLease
    {
        /// <inheritdoc />
        public SandboxTier Tier => SandboxTier.Tier1Jail;

        /// <inheritdoc />
        public string WorkingDirectory => workingDirectory;

        /// <inheritdoc />
        public ValueTask DisposeAsync() => release(workingDirectory);
    }
}

