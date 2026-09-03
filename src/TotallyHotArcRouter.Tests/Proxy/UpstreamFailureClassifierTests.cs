using System.Text;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers <see cref="UpstreamFailureClassifier"/>, the pure half of what one upstream response means:
/// which circuit-breaker signal it carries and whether the request fails over. These rules previously
/// lived as ~130 lines of interleaved classification and side effects roughly 350 lines deep inside
/// <c>ProxyMiddleware.InvokeCoreAsync</c>, reachable in tests only by standing up a middleware, a stub
/// handler, and an <c>HttpContext</c> - which is why the ADR-0004/0005 matrix below was only ever
/// covered at the two or three points <c>ProxyMiddlewareFallbackTests</c> exercises end-to-end. The
/// end-to-end tests stay as the integration proof; these pin the decision table itself.
/// </summary>
public class UpstreamFailureClassifierTests
{
    private const string OutOfCreditsMessage = "Your credit balance is too low to access the API.";

    private static UpstreamFailureVerdict Classify(
        int statusCode,
        bool isProviderAuthFailure = false,
        bool nextProviderDiffers = false,
        bool isExplicitPrimary = false,
        string? embeddedErrorMessage = null,
        byte[]? preReadErrorBody = null)
    {
        return UpstreamFailureClassifier.Classify(
            statusCode: statusCode,
            preReadErrorBody: preReadErrorBody,
            embeddedErrorMessage: embeddedErrorMessage,
            isProviderAuthFailure: isProviderAuthFailure,
            nextProviderDiffers: nextProviderDiffers,
            isExplicitPrimary: isExplicitPrimary);
    }

    // -- health signal --------------------------------------------------------

    [Theory]
    [InlineData(401, ProviderWideOutageCause.Unauthorized)]
    [InlineData(403, ProviderWideOutageCause.Forbidden)]
    [InlineData(405, ProviderWideOutageCause.MethodNotAllowed)]
    public void Classify_ProviderWideStatus_TripsWholeProviderWithItsOwnCause(int statusCode,
        ProviderWideOutageCause expected)
    {
        var verdict = Classify(statusCode);

        Assert.Equal(expected: ProviderHealthSignal.ProviderWideOutage, actual: verdict.HealthSignal);
        Assert.Equal(expected: expected, actual: verdict.ProviderWideCause);
    }

    [Fact]
    public void Classify_EmbeddedCredentialErrorOnNon401_TripsWholeProvider()
    {
        // Gemini's disguised 401: a 400 whose envelope says UNAUTHENTICATED. The status code alone cannot
        // tell this apart from a genuinely malformed request, which is why the translator's verdict is an
        // input here rather than something this method re-derives.
        var verdict = Classify(400, true);

        Assert.Equal(expected: ProviderHealthSignal.ProviderWideOutage, actual: verdict.HealthSignal);
        Assert.Equal(expected: ProviderWideOutageCause.EmbeddedCredentialError, actual: verdict.ProviderWideCause);
    }

    [Fact]
    public void Classify_401TakesPrecedenceOverEmbeddedCredentialError()
    {
        // Both conditions hold; the original if/else chain checked 401 first and the cause must not drift,
        // since the two log different operator-facing messages.
        var verdict = Classify(401, true);

        Assert.Equal(expected: ProviderWideOutageCause.Unauthorized, actual: verdict.ProviderWideCause);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(429)]
    public void Classify_OutOfCredits_TripsWholeProviderAndCarriesTheMessage(int statusCode)
    {
        // The 429 case is the ordering guard: out-of-credits must be checked ahead of the generic outage
        // bucket, or a classified-out-of-credits 429 falls into the weaker per-target failure. The whole
        // account is broken, not just this target.
        var verdict = Classify(statusCode: statusCode, embeddedErrorMessage: OutOfCreditsMessage);

        Assert.Equal(expected: ProviderHealthSignal.ProviderWideOutage, actual: verdict.HealthSignal);
        Assert.Equal(expected: ProviderWideOutageCause.OutOfCredits, actual: verdict.ProviderWideCause);
        Assert.True(verdict.IsOutOfCredits);
        Assert.Equal(expected: OutOfCreditsMessage, actual: verdict.OutOfCreditsMessage);
    }

    [Fact]
    public void Classify_OutOfCreditsFromBodyRatherThanEmbeddedMessage_IsAlsoDetected()
    {
        var body = Encoding.UTF8.GetBytes(
            """{"error":{"message":"You exceeded your current quota, please check your plan and billing details.","type":"insufficient_quota"}}""");

        var verdict = Classify(429, preReadErrorBody: body);

        Assert.True(verdict.IsOutOfCredits);
        Assert.Equal(expected: ProviderWideOutageCause.OutOfCredits, actual: verdict.ProviderWideCause);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(404)]
    public void Classify_OutageStatus_TripsOnlyThisTarget(int statusCode)
    {
        var verdict = Classify(statusCode);

        Assert.Equal(expected: ProviderHealthSignal.TargetOutage, actual: verdict.HealthSignal);
        Assert.Equal(expected: ProviderWideOutageCause.None, actual: verdict.ProviderWideCause);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(422)]
    public void Classify_TargetAnsweredAsConfigured_IsHealthy(int statusCode)
    {
        // A client-fault 4xx counts as healthy: the target answered as configured, the request was bad.
        var verdict = Classify(statusCode);

        Assert.Equal(expected: ProviderHealthSignal.TargetHealthy, actual: verdict.HealthSignal);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(204, true)]
    [InlineData(400, false)]
    [InlineData(422, false)]
    public void Classify_IsSuccessStatus_IsNarrowerThanHealthy(int statusCode, bool expected)
    {
        // ADR-0004: only a literal 2xx clears a live out-of-credits warning. A non-out-of-credits 400 is
        // "healthy" for circuit-breaker purposes but is not evidence the provider works in the billing
        // sense, so the two must not be conflated.
        Assert.Equal(expected: expected, actual: Classify(statusCode).IsSuccessStatus);
    }

    // -- failover -------------------------------------------------------------

    [Theory]
    [InlineData(500)]
    [InlineData(404)]
    public void Classify_UnconditionallyRetriableStatus_RetriesEvenOnSameProvider(int statusCode)
    {
        // A 5xx is a provider-side failure and a 404 means this target's model id is wrong or gone -
        // neither says anything about a different, already-configured candidate.
        Assert.True(Classify(statusCode: statusCode, nextProviderDiffers: false).ShouldRetry);
    }

    [Theory]
    [InlineData(429, false, false)]
    [InlineData(429, true, true)]
    public void Classify_PlainRateLimit_RetriesOnlyWhenTheNextCandidateIsADifferentProvider(
        int statusCode,
        bool nextProviderDiffers,
        bool expected)
    {
        // A same-provider backup shares the throttle, so retrying it only delays surfacing the failure.
        Assert.Equal(expected: expected,
            actual: Classify(statusCode: statusCode, nextProviderDiffers: nextProviderDiffers).ShouldRetry);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(422)]
    public void Classify_ClientFaultStatus_IsNeverRetried(int statusCode)
    {
        // A backup would reject the same request identically.
        Assert.False(Classify(statusCode: statusCode, nextProviderDiffers: true).ShouldRetry);
    }

    [Fact]
    public void Classify_ProviderWideStatus_ExplicitPrimary_DoesNotFailOverEvenAcrossProviders()
    {
        // docs/adr/0005: an explicit, never-substituted client selection on its first attempt relays the
        // real upstream error rather than silently routing somewhere the client did not ask for. This is
        // the rule the middleware previously enforced ~350 lines into a 715-line method.
        var verdict = Classify(401, nextProviderDiffers: true, isExplicitPrimary: true);

        Assert.False(verdict.ShouldRetry);
    }

    [Fact]
    public void Classify_ProviderWideStatus_AutoRouted_FailsOverToADifferentProvider()
    {
        Assert.True(Classify(401, nextProviderDiffers: true, isExplicitPrimary: false).ShouldRetry);
    }

    [Fact]
    public void Classify_ProviderWideStatus_SameProviderBackup_DoesNotFailOver()
    {
        // The backup shares the broken credential.
        Assert.False(Classify(401, nextProviderDiffers: false, isExplicitPrimary: false).ShouldRetry);
    }

    [Fact]
    public void Classify_PlainRateLimit_ExplicitPrimary_StillRetries()
    {
        // ADR-0005's boundary: a plain 429 is target-level, not provider-wide, so the explicit-primary
        // carve-out does not apply to it. Regressing this would silently stop failing over for explicitly
        // pinned models under load.
        Assert.True(Classify(429, nextProviderDiffers: true, isExplicitPrimary: true).ShouldRetry);
    }

    [Fact]
    public void Classify_OutOfCredits_ExplicitPrimary_DoesNotFailOver()
    {
        // Out-of-credits is provider-wide, so unlike a plain 429 it does get the explicit-primary carve-out.
        var verdict = Classify(
            429,
            nextProviderDiffers: true,
            isExplicitPrimary: true,
            embeddedErrorMessage: OutOfCreditsMessage);

        Assert.False(verdict.ShouldRetry);
    }

    [Fact]
    public void Classify_UnrecognizedErrorBody_IsNeverClassifiedAsOutOfCredits()
    {
        // Fails closed: misclassifying an ordinary client error would trip the provider-wide breaker for
        // every other model on that provider.
        var verdict = Classify(400, preReadErrorBody: Encoding.UTF8.GetBytes("not json at all"));

        Assert.False(verdict.IsOutOfCredits);
        Assert.Equal(expected: ProviderHealthSignal.TargetHealthy, actual: verdict.HealthSignal);
        Assert.Empty(verdict.OutOfCreditsMessage);
    }

    [Fact]
    public void Classify_NoPreReadBody_DoesNotThrow()
    {
        // The common path: most statuses never pre-read a body at all, so the classifier must tolerate null.
        var verdict = Classify(500);

        Assert.False(verdict.IsOutOfCredits);
        Assert.Empty(verdict.OutOfCreditsMessage);
    }
}