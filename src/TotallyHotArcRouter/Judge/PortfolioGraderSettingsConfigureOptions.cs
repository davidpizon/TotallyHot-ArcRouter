using Microsoft.Extensions.Options;
using System.Reflection;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Layers <see cref="RouterSettingsStore"/>'s stored overrides onto <see cref="PortfolioGraderOptions"/>, the
/// exact counterpart of <see cref="JudgeSettingsConfigureOptions"/> for the Q3 grader portfolio. Each of the
/// three flags is independently overridable: an operator may enable CodeJudge without ICE-Score, say.
/// </summary>
/// <remarks>
/// The coded default for each flag is computed the same way <see cref="JudgeOptions.Enabled"/>'s is: absent
/// a stored row, a flag turns on when an eligible free backbone exists (the same
/// <see cref="JudgeModelSelector.EnumerateEligible(IModelRouteResolver, ILogger)"/> predicate the judge
/// uses - the portfolio graders share its backbone) and stays off when none does. Uses the same
/// reflection-over-<c>init</c>-properties technique as <see cref="JudgeSettingsConfigureOptions"/> - see its
/// remarks for why.
/// </remarks>
public sealed class PortfolioGraderSettingsConfigureOptions : IConfigureOptions<PortfolioGraderOptions>
{
    private static readonly PropertyInfo CodeJudgeEnabledProperty =
        typeof(PortfolioGraderOptions).GetProperty(nameof(PortfolioGraderOptions.CodeJudgeEnabled))!;

    private static readonly PropertyInfo IceScoreEnabledProperty =
        typeof(PortfolioGraderOptions).GetProperty(nameof(PortfolioGraderOptions.IceScoreEnabled))!;

    private static readonly PropertyInfo RaceEnabledProperty =
        typeof(PortfolioGraderOptions).GetProperty(nameof(PortfolioGraderOptions.RaceEnabled))!;

    private readonly ILogger<PortfolioGraderSettingsConfigureOptions> _logger;
    private readonly IModelRouteResolver _routeResolver;
    private readonly RouterSettingsStore _store;

    /// <summary>Initializes a new instance of the <see cref="PortfolioGraderSettingsConfigureOptions"/> class.</summary>
    /// <param name="store">The settings store to read overrides from.</param>
    /// <param name="routeResolver">Supplies the configured models, used to decide whether any eligible free backbone exists.</param>
    /// <param name="logger">The logger.</param>
    public PortfolioGraderSettingsConfigureOptions(
        RouterSettingsStore store,
        IModelRouteResolver routeResolver,
        ILogger<PortfolioGraderSettingsConfigureOptions> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(routeResolver);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _routeResolver = routeResolver;
        _logger = logger;
    }

    /// <inheritdoc/>
    public void Configure(PortfolioGraderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Computed once per Configure call - shared across the three flags below, since the underlying
        // question ("does any free backbone exist right now") is the same for all of them.
        var hasBackbone = new Lazy<bool>(() =>
            JudgeModelSelector.EnumerateEligible(routeResolver: _routeResolver, logger: _logger).Any());

        ApplyFlag(RouterSettingsStore.CodeJudgeEnabledKey, CodeJudgeEnabledProperty, options, hasBackbone);
        ApplyFlag(RouterSettingsStore.IceScoreEnabledKey, IceScoreEnabledProperty, options, hasBackbone);
        ApplyFlag(RouterSettingsStore.RaceEnabledKey, RaceEnabledProperty, options, hasBackbone);
    }

    /// <summary>Applies one flag's stored override, or its computed default when no row exists.</summary>
    private void ApplyFlag(string key, PropertyInfo property, PortfolioGraderOptions options, Lazy<bool> hasBackbone)
    {
        if (_store.TryGetBool(key: key, value: out var enabled))
            property.SetValue(obj: options, value: enabled);
        else
            property.SetValue(obj: options, value: hasBackbone.Value);
    }
}
