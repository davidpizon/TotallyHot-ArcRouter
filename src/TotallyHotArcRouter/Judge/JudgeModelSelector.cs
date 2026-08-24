using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Resolves which model the shadow judge should call: a <b>free</b> model the operator already configured
/// in the Providers screen, rather than a hardcoded local endpoint. Replaces the removed
/// <c>JudgeOptions.BaseUrl</c>/<c>JudgeOptions.Model</c> pair, so the judge's "local and free" constraint
/// (docs/router/regret-evaluation-harness-plan.md's no-paid-backend rule) is enforced from the operator's
/// own <see cref="Models.ProviderOptions.IsFree"/> flag instead of from a default string nothing validated.
/// </summary>
/// <remarks>
/// <para>
/// Eligibility mirrors <c>RequestInterceptor.GetEligibleRoutes</c>: the model must resolve, its provider
/// must be switched on, and the model itself must be started and still reported upstream. Two further
/// restrictions are specific to judging:
/// </para>
/// <para>
/// <b>Free only.</b> <see cref="ResolvedModelRoute.IsFree"/> is the operator's explicit "this costs
/// nothing" flag - a <em>known</em> zero, as distinct from a model the price catalog simply has no data
/// for. Judging runs on every scored request, so an accidentally-paid backbone would bill continuously in
/// the background; requiring the explicit flag makes that impossible rather than unlikely.
/// </para>
/// <para>
/// <b>OpenAI-shaped only.</b> <see cref="GEvalJudgeClient"/> speaks OpenAI chat-completions directly,
/// bypassing the proxy's translation layer, so a Bedrock route (<see cref="ResolvedModelRoute.AwsRegion"/>
/// non-null) cannot serve it - that path needs the AWS SigV4 SDK client, not an HTTP header. Such a route
/// is skipped rather than attempted and failed, and the skip is logged so it is visible why an otherwise
/// free provider never appears in the dropdown.
/// </para>
/// <para>
/// Returning <see langword="null"/> - no free provider configured at all - is an honest state, not an
/// error: the judge abstains and writes no row, the same posture <c>LogRegVoter</c> and
/// <c>ClusterBestVoter</c> take when their artifact is absent.
/// </para>
/// </remarks>
public sealed class JudgeModelSelector
{
    private readonly IModelRouteResolver _routeResolver;
    private readonly IOptionsMonitor<JudgeOptions> _options;
    private readonly ILogger<JudgeModelSelector> _logger;

    /// <summary>Guards the substitution log below so a persistent fallback logs once, not once per scored request.</summary>
    private string? _lastLoggedSubstitution;

    /// <summary>Initializes a new instance of the <see cref="JudgeModelSelector"/> class.</summary>
    /// <param name="routeResolver">Supplies the configured models and their resolved provider routes.</param>
    /// <param name="options">Supplies the operator's chosen <see cref="JudgeOptions.ModelName"/>, read live so a settings change takes effect without a restart.</param>
    /// <param name="logger">The logger.</param>
    public JudgeModelSelector(
        IModelRouteResolver routeResolver,
        IOptionsMonitor<JudgeOptions> options,
        ILogger<JudgeModelSelector> logger)
    {
        ArgumentNullException.ThrowIfNull(routeResolver);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _routeResolver = routeResolver;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Lists the client-facing names of every model currently eligible to serve as the judge backbone, in
    /// configuration order. Backs the System Settings window's judge-model dropdown; sharing this one
    /// method with <see cref="Resolve"/> is what stops the offered choices and the accepted choices from
    /// drifting apart.
    /// </summary>
    /// <returns>The eligible model names; empty when no free provider is configured or enabled.</returns>
    public IReadOnlyList<string> ListEligibleModels() =>
        EnumerateEligible().Select(route => route.ModelName).ToList();

    /// <summary>
    /// Resolves the route the judge should call for this scoring attempt: the operator's chosen model when
    /// it is still eligible, otherwise the first eligible free model.
    /// </summary>
    /// <returns>The resolved route, or <see langword="null"/> when no free model is currently eligible.</returns>
    public ResolvedModelRoute? Resolve()
    {
        var eligible = EnumerateEligible().ToList();
        if (eligible.Count == 0)
        {
            _logger.LogDebug("No free, enabled, OpenAI-compatible model is configured; the shadow judge has nothing to call.");
            return null;
        }

        var chosenName = _options.CurrentValue.ModelName;
        if (string.IsNullOrWhiteSpace(chosenName))
        {
            return eligible[0];
        }

        var match = eligible.FirstOrDefault(route =>
            string.Equals(route.ModelName, chosenName, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        // Logged once per distinct substitution rather than per request: the judge scores continuously, and
        // a provider left switched off would otherwise emit this on every single scored response.
        if (!string.Equals(_lastLoggedSubstitution, chosenName, StringComparison.OrdinalIgnoreCase))
        {
            _lastLoggedSubstitution = chosenName;
            _logger.LogInformation(
                "Configured judge model {ConfiguredModel} is not currently eligible; falling back to {FallbackModel}.",
                chosenName,
                eligible[0].ModelName);
        }

        return eligible[0];
    }

    /// <summary>
    /// Yields every configured model that can currently serve as the judge backbone, in configuration
    /// order. See this type's remarks for why each condition is required.
    /// </summary>
    private IEnumerable<ResolvedModelRoute> EnumerateEligible()
    {
        foreach (var candidate in _routeResolver.ListModels())
        {
            if (!_routeResolver.TryResolve(candidate.ModelName, out var route))
            {
                continue;
            }

            if (!route.IsFree ||
                !_routeResolver.IsProviderEnabled(route.Provider) ||
                !_routeResolver.IsModelEnabled(candidate.ModelName))
            {
                continue;
            }

            if (route.AwsRegion is not null)
            {
                _logger.LogDebug(
                    "Skipping free model {ModelName} as a judge backbone: Bedrock routes are not OpenAI chat-completions shaped.",
                    candidate.ModelName);
                continue;
            }

            yield return route;
        }
    }
}
