using Microsoft.Extensions.Options;
using System.Reflection;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Layers <see cref="RouterSettingsStore"/>'s stored overrides onto <see cref="JudgeOptions"/>, the exact
/// counterpart of <see cref="RouterSettingsConfigureOptions"/> for <c>RoutingOptions</c>. The shadow
/// judge's two operator-facing settings - <see cref="JudgeOptions.Enabled"/> and
/// <see cref="JudgeOptions.ModelName"/> - live only in the <c>router_settings</c> table and the System
/// Settings window that writes it; <see cref="JudgeOptions"/> is deliberately not bound from
/// <c>appsettings.json</c> at all, so the precedence chain here is just
/// <b>
/// stored override &gt; coded
/// default
/// </b>
/// rather than that type's three-level one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The coded default for <see cref="JudgeOptions.Enabled"/> is computed, not constant.</b> With no
/// stored row, the judge turns itself on when an eligible free backbone exists and stays off when none
/// does, applying <see cref="JudgeModelSelector.EnumerateEligible(IModelRouteResolver, ILogger)"/> - the
/// shared predicate - rather than holding a <see cref="JudgeModelSelector"/>, which would close a DI cycle
/// back through the options factory this type is part of. That auto-detect lives here rather than on
/// <see cref="JudgeOptions"/> because it is a <em>default</em>, not a gate: an operator who has explicitly
/// switched the judge off in System Settings must stay switched off however many free models appear later,
/// and the stored-override-wins precedence below is what guarantees that. The reason for defaulting on at
/// all is that the judge stopped being an optional analysis aid when it became one of the two graders
/// feeding router memory - leaving it off by default would ship a verifier running at half strength for
/// anyone who never found the toggle.
/// </para>
/// <para>
/// Uses the same reflection-over-<c>init</c>-properties technique
/// (<see cref="PropertyInfo.SetValue(object?, object?)"/> is not bound by the compile-time-only <c>init</c>
/// guard) and the same "a missing row means no override" rule for
/// <see cref="JudgeOptions.ModelName"/>: an absent key leaves the coded default untouched rather than
/// re-asserting it.
/// </para>
/// </remarks>
public sealed class JudgeSettingsConfigureOptions : IConfigureOptions<JudgeOptions>
{
    private static readonly PropertyInfo EnabledProperty =
        typeof(JudgeOptions).GetProperty(nameof(JudgeOptions.Enabled))!;

    private static readonly PropertyInfo ModelNameProperty =
        typeof(JudgeOptions).GetProperty(nameof(JudgeOptions.ModelName))!;

    private readonly ILogger<JudgeSettingsConfigureOptions> _logger;
    private readonly IModelRouteResolver _routeResolver;

    private readonly RouterSettingsStore _store;

    /// <summary>Initializes a new instance of the <see cref="JudgeSettingsConfigureOptions"/> class.</summary>
    /// <param name="store">The settings store to read overrides from.</param>
    /// <param name="routeResolver">Supplies the configured models, used to decide whether any eligible free backbone exists.</param>
    /// <param name="logger">The logger.</param>
    public JudgeSettingsConfigureOptions(
        RouterSettingsStore store,
        IModelRouteResolver routeResolver,
        ILogger<JudgeSettingsConfigureOptions> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(routeResolver);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _routeResolver = routeResolver;
        _logger = logger;
    }

    /// <inheritdoc/>
    public void Configure(JudgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_store.TryGetBool(key: RouterSettingsStore.JudgeEnabledKey, value: out var enabled))
        {
            EnabledProperty.SetValue(obj: options, value: enabled);
        }
        else
        {
            // No stored row: fall back to "on if a free backbone exists". Any eligible model will do here -
            // which one the judge ultimately picks is JudgeModelSelector.Resolve()'s business, and asking
            // it would need JudgeOptions.ModelName, which is the value being configured.
            var hasBackbone = JudgeModelSelector.EnumerateEligible(routeResolver: _routeResolver, logger: _logger)
                .Any();
            EnabledProperty.SetValue(obj: options, value: hasBackbone);
        }

        if (_store.TryGetString(key: RouterSettingsStore.JudgeModelNameKey, value: out var modelName))
            ModelNameProperty.SetValue(obj: options, value: modelName);
    }
}