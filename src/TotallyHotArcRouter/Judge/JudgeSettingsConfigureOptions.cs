using System.Reflection;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Layers <see cref="RouterSettingsStore"/>'s stored overrides onto <see cref="JudgeOptions"/>, the exact
/// counterpart of <see cref="RouterSettingsConfigureOptions"/> for <c>RoutingOptions</c>. The shadow
/// judge's two operator-facing settings - <see cref="JudgeOptions.Enabled"/> and
/// <see cref="JudgeOptions.ModelName"/> - live only in the <c>router_settings</c> table and the System
/// Settings window that writes it; <see cref="JudgeOptions"/> is deliberately not bound from
/// <c>appsettings.json</c> at all, so the precedence chain here is just <b>stored override &gt; coded
/// default</b> rather than that type's three-level one.
/// </summary>
/// <remarks>
/// Uses the same reflection-over-<c>init</c>-properties technique
/// (<see cref="PropertyInfo.SetValue(object?, object?)"/> is not bound by the compile-time-only <c>init</c>
/// guard) and the same "a missing row means no override" rule: an absent key leaves the coded default
/// untouched rather than re-asserting it.
/// </remarks>
public sealed class JudgeSettingsConfigureOptions : IConfigureOptions<JudgeOptions>
{
    private static readonly PropertyInfo EnabledProperty =
        typeof(JudgeOptions).GetProperty(nameof(JudgeOptions.Enabled))!;

    private static readonly PropertyInfo ModelNameProperty =
        typeof(JudgeOptions).GetProperty(nameof(JudgeOptions.ModelName))!;

    private readonly RouterSettingsStore _store;

    /// <summary>Initializes a new instance of the <see cref="JudgeSettingsConfigureOptions"/> class.</summary>
    /// <param name="store">The settings store to read overrides from.</param>
    public JudgeSettingsConfigureOptions(RouterSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public void Configure(JudgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_store.TryGetBool(RouterSettingsStore.JudgeEnabledKey, out var enabled))
        {
            EnabledProperty.SetValue(options, enabled);
        }

        if (_store.TryGetString(RouterSettingsStore.JudgeModelNameKey, out var modelName))
        {
            ModelNameProperty.SetValue(options, modelName);
        }
    }
}
