namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// Configuration for the passkey content gate (ADR-0020), bound from the <c>Passkey</c> configuration
/// section. TTLs for challenges and one-operation authorizations stay fixed in code; only content-grant
/// duration and challenge-issuance rate limits are operator-tunable here.
/// </summary>
public sealed class PasskeyOptions
{
    /// <summary>Gets the configuration section name used for passkey settings.</summary>
    public const string SectionName = "Passkey";

    /// <summary>
    /// Gets how long a content grant remains valid after a successful unlock verification. Defaults to
    /// 15 minutes; callers clamp this to <c>[1, 60]</c> via <see cref="GetEffectiveContentGrantMinutes"/>.
    /// </summary>
    public int ContentGrantMinutes { get; init; } = 15;

    /// <summary>
    /// Gets the sustained rate of WebAuthn challenges the router will issue per minute before the global
    /// token bucket refuses new ones. Defaults to 10.
    /// </summary>
    public int ChallengeIssuancePerMinute { get; init; } = 10;

    /// <summary>
    /// Gets the burst capacity of the challenge-issuance token bucket. Defaults to 5.
    /// </summary>
    public int ChallengeIssuanceBurst { get; init; } = 5;

    /// <summary>
    /// Returns <see cref="ContentGrantMinutes"/> clamped to the ADR-0020 plan range of 1–60 minutes so a
    /// misconfiguration cannot disable the gate or grant multi-hour read windows by accident.
    /// </summary>
    public int GetEffectiveContentGrantMinutes()
    {
        return Math.Clamp(ContentGrantMinutes, min: 1, max: 60);
    }
}
