using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Sandbox.Execution;

/// <summary>
/// Default tier selector: non-executable content and degraded hosts stay on Tier 0; short executable
/// snippets run in a Tier 1 jail; large snippets escalate to a Tier 2 microVM when KVM is available.
/// </summary>
public sealed class TierSelector : ITierSelector
{
    private readonly ISandboxCapabilityProbe _probe;
    private readonly SandboxOptions _options;

    /// <summary>Initializes a new instance of the <see cref="TierSelector"/> class.</summary>
    /// <param name="probe">The capability probe reporting host isolation support.</param>
    /// <param name="options">The sandbox options carrying tier thresholds.</param>
    public TierSelector(ISandboxCapabilityProbe probe, IOptions<SandboxOptions> options)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(options);
        _probe = probe;
        _options = options.Value;
    }

    /// <inheritdoc />
    public SandboxTier Select(SandboxRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.TierHint is { } hint)
        {
            return hint;
        }

        if (!_probe.IsExecutionAvailable || !SandboxLanguages.IsExecutable(request.Language))
        {
            return SandboxTier.Tier0Static;
        }

        if (_probe.IsKvmAvailable && request.Code.Length >= _options.Tier2MinCodeBytes)
        {
            return SandboxTier.Tier2MicroVm;
        }

        return SandboxTier.Tier1Jail;
    }
}

