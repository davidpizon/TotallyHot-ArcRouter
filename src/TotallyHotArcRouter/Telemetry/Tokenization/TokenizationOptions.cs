using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Telemetry.Tokenization;

/// <summary>
/// Configuration for counterfactual token counting and its calibration loop (ADR-0009), bound from the
/// <c>CostTracking:Tokenization</c> section.
/// </summary>
/// <remarks>
/// Counting itself is always on and needs no configuration - it is local, offline, and free. Everything
/// here governs <em>calibration</em>, which is the only part that reaches the network, and which is
/// therefore off unless an operator turns it on.
/// </remarks>
public sealed class TokenizationOptions
{
    /// <summary>Gets the configuration section name used for tokenization settings.</summary>
    public const string SectionName = "CostTracking:Tokenization";

    /// <summary>
    /// Gets whether the calibration sampler may call a provider's token-counting endpoint.
    /// <see langword="false"/> by default.
    /// </summary>
    /// <remarks>
    /// Defaulting this off is a deliberate security posture, not caution about cost (Anthropic's
    /// <c>count_tokens</c> endpoint is free). ADR-0009 adds a second outbound egress destination to a
    /// process that proxies other people's prompts, so it stays inert until an operator opts in. With it
    /// off, counts are still produced - reported as
    /// <see cref="TokenCountSource.LocalUncalibrated"/> rather than
    /// <see cref="TokenCountSource.LocalCalibrated"/>.
    /// </remarks>
    public bool CalibrationEnabled { get; init; }

    /// <summary>
    /// Gets the name of the environment variable holding the ordinary Anthropic inference API key the
    /// calibration sampler authenticates with. Empty by default, which leaves the sampler inert even if
    /// <see cref="CalibrationEnabled"/> is set.
    /// </summary>
    /// <remarks>
    /// Deliberately its own setting rather than borrowing a key the router already holds for proxying.
    /// ADR-0009 adds an egress path, and an operator enabling it should have to say which credential it
    /// may use - reusing a routing key by default would widen what that key is spent on without anyone
    /// choosing it. Distinct again from <see cref="ProviderReconciliationOptions.AdminApiKeyEnvVar"/>:
    /// <c>count_tokens</c> neither needs nor accepts the Admin key.
    /// </remarks>
    public string ApiKeyEnvVar { get; init; } = string.Empty;

    /// <summary>Gets how often the calibration sampler runs a cycle, in minutes. Default 60.</summary>
    public int CycleIntervalMinutes { get; init; } = 60;

    /// <summary>
    /// Gets the maximum number of prompts sampled per cycle. Default 20 - low enough that the sampler
    /// cannot approach the endpoint's own rate limit even on a busy install, since calibration converges
    /// over days rather than needing volume.
    /// </summary>
    public int MaxSamplesPerCycle { get; init; } = 20;

    /// <summary>
    /// Gets the number of samples a model's factor must accumulate before it is applied. Default 10.
    /// </summary>
    /// <remarks>
    /// Below this, a factor is still recorded but reported as untrusted, so a single unrepresentative
    /// prompt cannot swing every counterfactual for a model. This is the same
    /// "thin evidence is not evidence" rule the conditioned output estimator applies to sparse cells.
    /// </remarks>
    public int MinSamplesForTrust { get; init; } = 10;

    /// <summary>
    /// Gets the weight given to the newest sample when folding it into a model's running factor, in
    /// <c>(0, 1]</c>. Default 0.2.
    /// </summary>
    /// <remarks>
    /// An exponentially-weighted mean rather than a simple average: it needs no sample history on disk,
    /// tracks a genuine tokenizer change when a provider ships one, and bounds how far a single outlier
    /// can move the factor to this fraction of the gap.
    /// </remarks>
    public double SmoothingFactor { get; init; } = 0.2d;

    /// <summary>
    /// Performs domain validation not expressible through data annotations, mirroring
    /// <see cref="CostReconciliationOptions.EnsureValid"/> - fail at startup rather than when
    /// <see cref="PeriodicTimer"/> rejects a non-positive period or a nonsensical factor silently
    /// corrupts every count.
    /// </summary>
    /// <exception cref="OptionsValidationException">Thrown when any value is outside its permitted range.</exception>
    public void EnsureValid()
    {
        List<string> failures = [];

        if (CycleIntervalMinutes < 1)
            failures.Add($"CycleIntervalMinutes must be at least 1 (was {CycleIntervalMinutes}).");

        if (MaxSamplesPerCycle < 1)
            failures.Add($"MaxSamplesPerCycle must be at least 1 (was {MaxSamplesPerCycle}).");

        if (MinSamplesForTrust < 1)
            failures.Add($"MinSamplesForTrust must be at least 1 (was {MinSamplesForTrust}).");

        if (!double.IsFinite(SmoothingFactor) || SmoothingFactor is <= 0d or > 1d)
            failures.Add($"SmoothingFactor must be in (0, 1] (was {SmoothingFactor}).");

        if (failures.Count > 0)
            throw new OptionsValidationException(
                optionsName: nameof(TokenizationOptions),
                optionsType: typeof(TokenizationOptions),
                failureMessages: failures);
    }
}
