using Microsoft.Extensions.Options;
using System.Reflection;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Layers <see cref="RouterSettingsStore"/>'s stored overrides onto <see cref="RoutingOptions"/>
/// (docs/router/self-organizing-classification-plan.md Phase T6): the last
/// <c>IConfigureOptions&lt;RoutingOptions&gt;</c> step registered, so it runs after the
/// <c>appsettings.json</c>-binding step and wins - <b>stored override &gt; appsettings.json &gt; coded
/// default</b>. Only ever overwrites <see cref="RoutingOptions.EnableAdaptiveRouting"/> and
/// <see cref="RoutingOptions.EmbeddingMemoryCapacity"/>, and only when a row actually exists for the
/// corresponding key - a missing row means "no override", leaving whatever the earlier steps already
/// produced untouched rather than re-asserting the coded default a second time.
/// </summary>
/// <remarks>
/// <see cref="RoutingOptions"/>'s properties are <c>init</c>-only, matching every other property on the
/// type; this step overwrites them after construction via <see cref="PropertyInfo.SetValue(object?, object?)"/>,
/// which - unlike ordinary C# assignment - is not restricted by the <c>init</c> accessor's compile-time-only
/// guard. This mirrors how <c>IConfiguration.Bind</c> already sets every other property on this same type
/// through reflection in the preceding configure step; no different in kind, just written out explicitly
/// since there are only two properties to touch rather than the whole type.
/// </remarks>
public sealed class RouterSettingsConfigureOptions : IConfigureOptions<RoutingOptions>
{
    private static readonly PropertyInfo EnableAdaptiveRoutingProperty =
        typeof(RoutingOptions).GetProperty(nameof(RoutingOptions.EnableAdaptiveRouting))!;

    private static readonly PropertyInfo EmbeddingMemoryCapacityProperty =
        typeof(RoutingOptions).GetProperty(nameof(RoutingOptions.EmbeddingMemoryCapacity))!;

    private readonly RouterSettingsStore _store;

    /// <summary>Initializes a new instance of the <see cref="RouterSettingsConfigureOptions"/> class.</summary>
    /// <param name="store">The settings store to read overrides from.</param>
    public RouterSettingsConfigureOptions(RouterSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public void Configure(RoutingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_store.TryGetBool(RouterSettingsStore.AdaptiveRoutingEnabledKey, out var adaptiveRoutingEnabled))
        {
            EnableAdaptiveRoutingProperty.SetValue(options, adaptiveRoutingEnabled);
        }

        if (_store.TryGetInt(RouterSettingsStore.EmbeddingMemoryCapacityKey, out var embeddingMemoryCapacity))
        {
            EmbeddingMemoryCapacityProperty.SetValue(options, embeddingMemoryCapacity);
        }
    }
}
