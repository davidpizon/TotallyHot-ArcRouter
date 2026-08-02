using TotallyHot.ArcRouter.Sandbox.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Sandbox.Tier1;

/// <summary>
/// Tier-1 runtime: leases a warm jail, writes the snippet into its per-lease working directory, and runs
/// it through the <see cref="IJailLauncher"/> under namespaces + cgroups + supervisor. The lease is reset
/// on disposal regardless of outcome.
/// </summary>
public sealed class Tier1JailRuntime : ISandboxRuntime
{
    private readonly ISandboxPool _pool;
    private readonly IJailLauncher _launcher;
    private readonly SandboxOptions _options;
    private readonly ILogger<Tier1JailRuntime> _logger;

    /// <summary>Initializes a new instance of the <see cref="Tier1JailRuntime"/> class.</summary>
    /// <param name="pools">The registered pools; the Tier-1 pool is selected from these.</param>
    /// <param name="launcher">The jail launcher.</param>
    /// <param name="options">The sandbox options (limits, timeout).</param>
    /// <param name="logger">The logger.</param>
    public Tier1JailRuntime(
        IEnumerable<ISandboxPool> pools,
        IJailLauncher launcher,
        IOptions<SandboxOptions> options,
        ILogger<Tier1JailRuntime> logger)
    {
        ArgumentNullException.ThrowIfNull(pools);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _pool = pools.FirstOrDefault(p => p.Tier == SandboxTier.Tier1Jail)
            ?? throw new InvalidOperationException("No Tier-1 sandbox pool is registered.");
        _launcher = launcher;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public SandboxTier Tier => SandboxTier.Tier1Jail;

    /// <inheritdoc />
    public async Task<ExecutionOutcome> ExecuteAsync(SandboxRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!JailCommandBuilder.IsSupported(request.Language))
        {
            // The tier selector should never route non-executable content here; guard defensively.
            return new ExecutionOutcome { Stderr = $"No Tier-1 runtime for language '{request.Language}'." };
        }

        var command = JailCommandBuilder.Build(request.Language);

        await using var lease = await _pool.LeaseAsync(cancellationToken).ConfigureAwait(false);
        var scriptPath = Path.Combine(lease.WorkingDirectory, command.ScriptFileName);
        await File.WriteAllTextAsync(scriptPath, request.Code, cancellationToken).ConfigureAwait(false);

        var spec = new JailSpec
        {
            Interpreter = command.Interpreter,
            UnshareFlags = command.UnshareFlags,
            ScriptFileName = command.ScriptFileName,
            WorkingDirectory = lease.WorkingDirectory,
            TimeoutMs = _options.MaxWallClockMs,
            MemoryMax = CgroupLimits.MemoryMax(_options),
            PidsMax = CgroupLimits.PidsMax(_options),
            CpuMax = CgroupLimits.CpuMax,
        };

        _logger.LogDebug(
            "Tier 1 running {Interpreter} for {Language} (correlation {CorrelationId}).",
            command.Interpreter,
            request.Language,
            request.CorrelationId);

        return await _launcher.RunAsync(spec, cancellationToken).ConfigureAwait(false);
    }
}

