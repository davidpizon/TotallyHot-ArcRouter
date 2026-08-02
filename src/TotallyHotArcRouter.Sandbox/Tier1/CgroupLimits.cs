using System.Globalization;

namespace TotallyHot.ArcRouter.Sandbox.Tier1;

/// <summary>
/// Renders cgroup v2 control-file values from <see cref="SandboxOptions"/>. Pure/OS-agnostic so it is
/// unit-testable; <see cref="CgroupManager"/> writes these into the cgroup filesystem on Linux.
/// </summary>
public static class CgroupLimits
{
    /// <summary>The value written to <c>memory.max</c> (bytes).</summary>
    /// <param name="options">The sandbox options.</param>
    /// <returns>The memory ceiling as a decimal string.</returns>
    public static string MemoryMax(SandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.MemoryMaxBytes.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The value written to <c>memory.swap.max</c> — always 0 (no swap escape hatch).</summary>
    public static string SwapMax => "0";

    /// <summary>The value written to <c>pids.max</c>.</summary>
    /// <param name="options">The sandbox options.</param>
    /// <returns>The process cap as a decimal string.</returns>
    public static string PidsMax(SandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.PidsMax.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The value written to <c>cpu.max</c>: a quota and period bounding CPU to roughly one core-equivalent
    /// (<c>100000 100000</c> = 100 ms of runtime per 100 ms period).
    /// </summary>
    public static string CpuMax => "100000 100000";
}

