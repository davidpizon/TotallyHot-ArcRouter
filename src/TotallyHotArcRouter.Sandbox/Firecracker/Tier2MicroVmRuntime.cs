using TotallyHot.ArcRouter.Sandbox.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Sandbox.Firecracker;

/// <summary>
/// Tier-2 runtime: leases a warm microVM working area and runs the snippet through the
/// <see cref="IMicroVmLauncher"/>. The lease (per-run overlay/socket area) is reset on disposal.
/// </summary>
public sealed class Tier2MicroVmRuntime : ISandboxRuntime
{
    private readonly ISandboxPool _pool;
    private readonly IMicroVmLauncher _launcher;
    private readonly SandboxOptions _options;
    private readonly ILogger<Tier2MicroVmRuntime> _logger;

    /// <summary>Initializes a new instance of the <see cref="Tier2MicroVmRuntime"/> class.</summary>
    /// <param name="pools">The registered pools; the Tier-2 pool is selected from these.</param>
    /// <param name="launcher">The microVM launcher.</param>
    /// <param name="options">The sandbox options.</param>
    /// <param name="logger">The logger.</param>
    public Tier2MicroVmRuntime(
        IEnumerable<ISandboxPool> pools,
        IMicroVmLauncher launcher,
        IOptions<SandboxOptions> options,
        ILogger<Tier2MicroVmRuntime> logger)
    {
        ArgumentNullException.ThrowIfNull(pools);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _pool = pools.FirstOrDefault(p => p.Tier == SandboxTier.Tier2MicroVm)
            ?? throw new InvalidOperationException("No Tier-2 sandbox pool is registered.");
        _launcher = launcher;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public SandboxTier Tier => SandboxTier.Tier2MicroVm;

    /// <inheritdoc />
    public async Task<ExecutionOutcome> ExecuteAsync(SandboxRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var lease = await _pool.LeaseAsync(cancellationToken).ConfigureAwait(false);

        var spec = new MicroVmSpec
        {
            Language = request.Language,
            Code = request.Code,
            WorkingDirectory = lease.WorkingDirectory,
            TimeoutMs = _options.MaxWallClockMs,
            MemoryMaxBytes = _options.MemoryMaxBytes,
        };

        _logger.LogDebug(
            "Tier 2 running {Language} in a microVM (correlation {CorrelationId}).",
            request.Language,
            request.CorrelationId);

        return await _launcher.RunAsync(spec, cancellationToken).ConfigureAwait(false);
    }
}

