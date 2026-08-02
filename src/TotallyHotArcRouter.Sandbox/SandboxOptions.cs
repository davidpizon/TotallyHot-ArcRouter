namespace TotallyHot.ArcRouter.Sandbox;

/// <summary>
/// Configuration for the sandboxed executor, bound from the <c>Sandbox</c> section of configuration.
/// </summary>
public sealed class SandboxOptions
{
    /// <summary>The configuration section name for sandbox settings.</summary>
    public const string SectionName = "Sandbox";

    /// <summary>Whether the sandbox executor is enabled. Forced off by the capability probe when execution is unavailable.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Fraction of eligible requests actually evaluated (0..1). 1.0 evaluates every eligible request.</summary>
    public double SamplingRate { get; set; } = 1.0;

    /// <summary>Hard wall-clock ceiling per execution, in milliseconds, enforced by the external supervisor.</summary>
    public int MaxWallClockMs { get; set; } = 3000;

    /// <summary>The per-execution memory cgroup ceiling in bytes.</summary>
    public long MemoryMaxBytes { get; set; } = 134217728;

    /// <summary>The per-execution process/thread cap (fork-bomb protection).</summary>
    public int PidsMax { get; set; } = 64;

    /// <summary>The size of the per-lease writable tmpfs, in megabytes.</summary>
    public int TmpfsSizeMb { get; set; } = 8;

    /// <summary>Maximum characters of extracted code evaluated from a single block.</summary>
    public int MaxCodeBytes { get; set; } = 65536;

    /// <summary>Maximum number of fenced code blocks scanned per response.</summary>
    public int MaxCodeBlocks { get; set; } = 4;

    /// <summary>Capacity of the bounded work queue. Enqueues beyond this are dropped from sampling.</summary>
    public int QueueCapacity { get; set; } = 256;

    /// <summary>Maximum number of executions processed concurrently by the worker.</summary>
    public int WorkerConcurrency { get; set; } = 2;

    /// <summary>Whether a Tier-1 seccomp denial escalates the workload to a Tier-2 microVM.</summary>
    public bool EscalateOnSeccompDenial { get; set; } = true;

    /// <summary>Code size (in bytes) at or above which the tier selector prefers a Tier-2 microVM.</summary>
    public int Tier2MinCodeBytes { get; set; } = 16384;

    /// <summary>The guest runtimes carried by the base image.</summary>
    public List<string> Runtimes { get; set; } = ["python", "node", "shell"];

    /// <summary>
    /// Prefix applied to dimension keys when observing live scores into router memory, keeping heuristic
    /// live signals in a separate namespace from checked-in benchmark matrices (see architecture doc §15).
    /// </summary>
    public string LiveMemoryPrefix { get; set; } = "live:";

    /// <summary>Maximum characters of captured stdout/stderr retained on a result.</summary>
    public int MaxCapturedOutputBytes { get; set; } = 4096;

    /// <summary>Warm-pool sizing per execution tier.</summary>
    public WarmPoolOptions WarmPool { get; set; } = new();

    /// <summary>Tier-2 Firecracker microVM settings (binary/snapshot/kernel paths).</summary>
    public FirecrackerOptions Firecracker { get; set; } = new();

    /// <summary>Per-dimension scoring weights, keyed by dimension name.</summary>
    public Dictionary<string, DimensionWeightOptions> DimensionWeights { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves the scoring weights for a dimension, falling back to a balanced default.</summary>
    /// <param name="dimension">The dimension whose weights to resolve.</param>
    /// <returns>The configured weights, or <see cref="DimensionWeightOptions.Default"/>.</returns>
    public DimensionWeightOptions ResolveWeights(string dimension)
    {
        if (!string.IsNullOrEmpty(dimension) && DimensionWeights.TryGetValue(dimension, out var weights))
        {
            return weights;
        }

        return DimensionWeightOptions.Default;
    }
}

/// <summary>Warm-pool sizing per execution tier.</summary>
public sealed class WarmPoolOptions
{
    /// <summary>Number of pre-warmed Tier-1 native jails kept idle.</summary>
    public int Tier1 { get; set; } = 8;

    /// <summary>Number of pre-warmed Tier-2 microVM snapshots kept idle.</summary>
    public int Tier2 { get; set; } = 2;
}

/// <summary>Tier-2 Firecracker microVM settings. Paths are resolved at deployment; the capability probe and DI gate registration on their presence.</summary>
public sealed class FirecrackerOptions
{
    /// <summary>Path to the <c>firecracker</c> binary, or null to auto-locate on <c>PATH</c>/common locations.</summary>
    public string? BinaryPath { get; set; }

    /// <summary>Path to the optional <c>jailer</c> binary that wraps Firecracker in its own chroot/namespaces.</summary>
    public string? JailerPath { get; set; }

    /// <summary>Path to the pre-booted guest VM state snapshot file.</summary>
    public string? SnapshotPath { get; set; }

    /// <summary>Path to the guest memory snapshot file paired with <see cref="SnapshotPath"/>.</summary>
    public string? MemorySnapshotPath { get; set; }

    /// <summary>Directory holding per-run API sockets and Copy-on-Write overlays.</summary>
    public string? RuntimeDirectory { get; set; }

    /// <summary>The guest vsock context id used to reach the in-guest exec agent.</summary>
    public uint VsockGuestCid { get; set; } = 3;

    /// <summary>The vsock port the in-guest exec agent listens on.</summary>
    public uint VsockPort { get; set; } = 5252;

    /// <summary>Number of vCPUs configured for the microVM.</summary>
    public int VcpuCount { get; set; } = 1;

    /// <summary>
    /// Maximum time to wait for the Firecracker API socket to appear after the process starts, in
    /// milliseconds. Bounded by the run's remaining wall-clock budget regardless of this ceiling.
    /// </summary>
    public int ApiSocketWaitMs { get; set; } = 1000;

    /// <summary>
    /// Maximum time to wait for the vsock UDS to appear after the guest resumes, in milliseconds. Bounded
    /// by the run's remaining wall-clock budget regardless of this ceiling.
    /// </summary>
    public int VsockSocketWaitMs { get; set; } = 5000;
}

/// <summary>Per-dimension scoring weights for the unified score (weights need not pre-sum to 1; the scorer normalizes).</summary>
public sealed class DimensionWeightOptions
{
    /// <summary>The default balanced weighting used when a dimension has no explicit configuration.</summary>
    public static DimensionWeightOptions Default { get; } = new() { Syntax = 0.5, Exec = 0.5 };

    /// <summary>Weight applied to the structural-validity signal (s_syntax).</summary>
    public double Syntax { get; set; } = 0.5;

    /// <summary>Weight applied to the execution-outcome signal (s_exec).</summary>
    public double Exec { get; set; } = 0.5;
}

