namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// What one upstream response says about the health of the target that produced it. Distinguishes a
/// failure that condemns the whole provider from one that condemns only this target, because the two
/// take different circuit-breaker actions (<see cref="ICircuitBreaker.RecordProviderFailure"/> vs
/// <see cref="ICircuitBreaker.RecordFailure"/>) and different failover rules.
/// </summary>
public enum ProviderHealthSignal
{
    /// <summary>The target answered as configured. Clears this target's failure state.</summary>
    TargetHealthy,

    /// <summary>This one target is unhealthy (5xx, 429, or 404), but the provider itself may be fine.</summary>
    TargetOutage,

    /// <summary>Every model on this provider would fail identically - a credential, permission, gateway, or billing failure.</summary>
    ProviderWideOutage
}

/// <summary>
/// Why a response was judged <see cref="ProviderHealthSignal.ProviderWideOutage"/>. Carried separately
/// from the signal so the caller can log the specific, operator-actionable reason without re-deriving
/// it from the status code - each cause has a materially different remedy (rotate a key, widen a
/// permission scope, talk to the provider's gateway, top up credits).
/// </summary>
public enum ProviderWideOutageCause
{
    /// <summary>Not a provider-wide outage.</summary>
    None,

    /// <summary>A literal 401 - almost always an invalid or expired credential.</summary>
    Unauthorized,

    /// <summary>
    /// A non-401 status carrying an embedded credential error, reported by the translator via
    /// <see cref="Translation.EmbeddedProviderError.IsAuthFailure"/>. Gemini's 400-with-UNAUTHENTICATED is the case this
    /// exists for.
    /// </summary>
    EmbeddedCredentialError,

    /// <summary>
    /// A 403 - almost always a permission or API-key-scope problem rather than something specific to the requested
    /// model.
    /// </summary>
    Forbidden,

    /// <summary>
    /// A 405 on a path this proxy itself constructs - almost always a provider-side gateway/WAF rejecting the request
    /// at the edge.
    /// </summary>
    MethodNotAllowed,

    /// <summary>The account is out of credits (docs/adr/0004). The whole account is broken, not just this target.</summary>
    OutOfCredits
}

/// <summary>
/// The complete verdict on one upstream response: what it says about provider health, and whether the
/// request should fail over to the next candidate.
/// </summary>
/// <param name="HealthSignal">What to record against the circuit breaker.</param>
/// <param name="ProviderWideCause">
/// Why, when <paramref name="HealthSignal"/> is
/// <see cref="ProviderHealthSignal.ProviderWideOutage"/>; otherwise <see cref="ProviderWideOutageCause.None"/>.
/// </param>
/// <param name="IsOutOfCredits">Whether ADR-0004's out-of-credits classifier matched this body.</param>
/// <param name="OutOfCreditsMessage">
/// The operator-facing message when <paramref name="IsOutOfCredits"/> is set; otherwise
/// empty.
/// </param>
/// <param name="ShouldRetry">
/// Whether this candidate is worth failing over from, assuming a next candidate exists. Does
/// <em>not</em> account for whether one actually does - the caller still checks that.
/// </param>
/// <param name="IsSuccessStatus">
/// Whether the status is a literal 2xx. Narrower than
/// <see cref="ProviderHealthSignal.TargetHealthy"/>, which also covers client-fault 4xx like 400/422.
/// </param>
public readonly record struct UpstreamFailureVerdict(
    ProviderHealthSignal HealthSignal,
    ProviderWideOutageCause ProviderWideCause,
    bool IsOutOfCredits,
    string OutOfCreditsMessage,
    bool ShouldRetry,
    bool IsSuccessStatus);

/// <summary>
/// Decides what one upstream response means: which circuit-breaker signal it carries, and whether the
/// request should fail over. Extracted from <see cref="ProxyMiddleware.InvokeCoreAsync"/>'s candidate
/// loop, where it sat as ~130 lines of interleaved classification and side effects roughly 350 lines
/// deep inside a 715-line method.
/// <para>
/// Deliberately <see langword="static"/> and free of I/O, logging, and circuit-breaker mutation: this
/// is the logic failover regressions actually land in (see this file's tests, and the ADR-0004/0005
/// cases in <c>ProxyMiddlewareFallbackTests</c>), and it is only cheap to test exhaustively if
/// evaluating it cannot touch anything. The caller applies the verdict - see
/// <c>ProxyMiddleware.ApplyHealthSignal</c>, which is the half that logs and records.
/// </para>
/// </summary>
public static class UpstreamFailureClassifier
{
    /// <summary>
    /// Classifies one upstream response. Pure: same inputs always produce the same verdict, and
    /// evaluating it has no observable effect.
    /// </summary>
    /// <param name="statusCode">The upstream response's HTTP status code.</param>
    /// <param name="preReadErrorBody">
    /// The buffered error body, when one was pre-read; otherwise <see langword="null"/>. Only
    /// ADR-0004's out-of-credits classification reads it.
    /// </param>
    /// <param name="embeddedErrorMessage">The translator-decoded error message, when one was extracted.</param>
    /// <param name="isProviderAuthFailure">
    /// Whether the translator reported the embedded error as a credential failure (
    /// <see cref="Translation.EmbeddedProviderError.IsAuthFailure"/>).
    /// </param>
    /// <param name="nextProviderDiffers">
    /// Whether any remaining candidate is on a different provider - a separate quota pool
    /// and credential, which is the only thing that makes a 401/403/405/429 worth retrying.
    /// </param>
    /// <param name="isExplicitPrimary">
    /// Whether this is an explicit, never-substituted client selection on its first attempt.
    /// Per ADR-0005 such a request relays the truth instead of failing over on a provider-wide status.
    /// </param>
    public static UpstreamFailureVerdict Classify(
        int statusCode,
        byte[]? preReadErrorBody,
        string? embeddedErrorMessage,
        bool isProviderAuthFailure,
        bool nextProviderDiffers,
        bool isExplicitPrimary)
    {
        // docs/adr/0004-surface-out-of-credits-provider-failures-on-the-providers-tab.md: scoped to
        // 400/429 (Anthropic's real case is a 400; OpenAI's insufficient_quota is typically a 429).
        // Declared ahead of the short-circuiting && rather than as an `out var`: when the status gate is
        // false the call never runs, so the out parameter would be unassigned.
        var outOfCreditsMessage = string.Empty;
        var isOutOfCredits = (statusCode == StatusCodes.Status400BadRequest || statusCode == 429) &&
                             OutOfCreditsClassifier.IsOutOfCredits(body: preReadErrorBody ?? [],
                                 embeddedMessage: embeddedErrorMessage, message: out outOfCreditsMessage);

        // Order matters and mirrors the original chain exactly. Out-of-credits is checked ahead of the
        // generic outage bucket below, since a classified-out-of-credits 429 would otherwise fall into
        // that branch's weaker per-target failure - out-of-credits is always provider-wide (the whole
        // account is broken, not just this one target), like 401/403/405.
        var cause = statusCode switch
        {
            StatusCodes.Status401Unauthorized => ProviderWideOutageCause.Unauthorized,
            _ when isProviderAuthFailure => ProviderWideOutageCause.EmbeddedCredentialError,
            StatusCodes.Status403Forbidden => ProviderWideOutageCause.Forbidden,
            StatusCodes.Status405MethodNotAllowed => ProviderWideOutageCause.MethodNotAllowed,
            _ when isOutOfCredits => ProviderWideOutageCause.OutOfCredits,
            _ => ProviderWideOutageCause.None
        };

        var healthSignal = cause is not ProviderWideOutageCause.None
            ? ProviderHealthSignal.ProviderWideOutage
            : IsOutageStatus(statusCode)
                ? ProviderHealthSignal.TargetOutage
                : ProviderHealthSignal.TargetHealthy;

        // docs/adr/0004-.../0005-...: for a provider-wide status, an explicit primary (never substituted
        // so far, and this is its first attempt rather than an already-failed-over hop) does NOT retry
        // across providers even when nextProviderDiffers - it relays the real upstream error, exactly as
        // ADR-0005 requires. This overrides the generic 401/403/405/429 cross-provider carve-out
        // specifically for these provider-wide statuses; a plain 429 (not out-of-credits) is target-level
        // and keeps retrying unconditionally on explicit and auto alike, matching ADR-0005's explicit
        // "target-level trips unaffected" boundary for this same-request live-discovery cascade.
        var shouldRetry = healthSignal is ProviderHealthSignal.ProviderWideOutage
            ? nextProviderDiffers && !isExplicitPrimary
            : IsRetriableOutageStatus(statusCode: statusCode, nextBackupIsDifferentProvider: nextProviderDiffers);

        return new UpstreamFailureVerdict(
            HealthSignal: healthSignal,
            ProviderWideCause: cause,
            IsOutOfCredits: isOutOfCredits,
            OutOfCreditsMessage: outOfCreditsMessage,
            ShouldRetry: shouldRetry,
            IsSuccessStatus: statusCode is >= 200 and < 300);
    }

    /// <summary>
    /// Whether an upstream status counts as a per-target circuit-breaker failure: any 5xx, a 429 (rate
    /// limit), or a 404 (this target's configured model doesn't exist or is gone - a per-target
    /// misconfiguration, unlike 401/403/405, which are provider-wide). Unlike
    /// <see cref="IsRetriableOutageStatus"/>, this does not depend on whether a backup exists or shares
    /// the same provider - each of these statuses is proof <em>this</em> target is unhealthy right now
    /// regardless of what (if anything) the request fails over to next.
    /// </summary>
    internal static bool IsOutageStatus(int statusCode)
    {
        return statusCode is >= 500 and <= 599 or 429 or StatusCodes.Status404NotFound;
    }

    /// <summary>
    /// Whether an upstream status is worth failing over from. A 5xx (provider-side failure) or a 404
    /// (this target's configured model is wrong or gone, which says nothing about a different,
    /// already-configured candidate) retries unconditionally. A 429/401/403/405 retries only when
    /// <paramref name="nextBackupIsDifferentProvider"/> is <see langword="true"/>: a backup sharing the
    /// same provider shares the same quota pool, credential, permission scope, or gateway policy and
    /// would fail identically, so retrying it would only delay surfacing the failure to the client. All
    /// other statuses - including client-fault 4xx such as 400/422 - are never retried, since a backup
    /// would reject the same request the same way.
    /// </summary>
    internal static bool IsRetriableOutageStatus(int statusCode, bool nextBackupIsDifferentProvider)
    {
        if (statusCode is >= 500 and <= 599) return true;

        if (statusCode == StatusCodes.Status404NotFound) return true;

        return (statusCode == 429
                || statusCode == StatusCodes.Status401Unauthorized
                || statusCode == StatusCodes.Status403Forbidden
                || statusCode == StatusCodes.Status405MethodNotAllowed)
               && nextBackupIsDifferentProvider;
    }
}