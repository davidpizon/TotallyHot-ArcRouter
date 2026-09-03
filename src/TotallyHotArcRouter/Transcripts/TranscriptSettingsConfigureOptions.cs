using Microsoft.Extensions.Options;
using System.Reflection;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// Layers <see cref="RouterSettingsStore"/>'s stored override onto <see cref="TranscriptOptions.Enabled"/>,
/// the <see cref="TranscriptOptions"/> counterpart of <see cref="Router.RouterSettingsConfigureOptions"/>.
/// Registered as the last <c>IConfigureOptions&lt;TranscriptOptions&gt;</c> step, so it runs after the
/// <c>appsettings.json</c>-binding step and wins - <b>stored override &gt; appsettings.json &gt; coded
/// default</b>, the same three-level precedence <see cref="Router.RouterSettingsConfigureOptions"/>
/// documents for <see cref="Models.RoutingOptions"/>. Only ever overwrites <see cref="TranscriptOptions.Enabled"/>,
/// and only when a row actually exists for <see cref="RouterSettingsStore.TranscriptCaptureEnabledKey"/> - a
/// missing row means "no override", leaving whatever the earlier steps already produced untouched.
/// </summary>
/// <remarks>
/// <see cref="TranscriptOptions.Enabled"/> is an <c>init</c>-only property, matching every other property on
/// the type; this step overwrites it after construction via <see cref="PropertyInfo.SetValue(object?, object?)"/>,
/// which - unlike ordinary C# assignment - is not restricted by the <c>init</c> accessor's compile-time-only
/// guard. Mirrors how <c>IConfiguration.Bind</c> already sets every other property on this same type through
/// reflection in the preceding configure step.
/// </remarks>
public sealed class TranscriptSettingsConfigureOptions : IConfigureOptions<TranscriptOptions>
{
    private static readonly PropertyInfo EnabledProperty =
        typeof(TranscriptOptions).GetProperty(nameof(TranscriptOptions.Enabled))!;

    private readonly RouterSettingsStore _store;

    /// <summary>Initializes a new instance of the <see cref="TranscriptSettingsConfigureOptions"/> class.</summary>
    /// <param name="store">The settings store to read the override from.</param>
    public TranscriptSettingsConfigureOptions(RouterSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public void Configure(TranscriptOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_store.TryGetBool(RouterSettingsStore.TranscriptCaptureEnabledKey, out var enabled))
        {
            EnabledProperty.SetValue(options, enabled);
        }
    }
}
