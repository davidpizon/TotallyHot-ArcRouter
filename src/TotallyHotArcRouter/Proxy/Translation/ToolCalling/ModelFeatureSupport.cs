using System.Text.Json;

namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// What one upstream model says it supports, as its provider's own model list reported it - Anthropic's Models API
/// <c>capabilities</c> object (<c>docs/adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md</c> Amendment 1).
/// <para>
/// Kept as the vendor's raw JSON rather than a typed projection, and read by key name. The request-path stripper
/// asks "is the thinking type this request names supported?" or "is this context-management strategy supported?"
/// using the names the request itself carries, so a strategy or effort level Anthropic adds later needs no code
/// change and no migration, and no rule here names a model, family or version.
/// </para>
/// </summary>
/// <param name="ProviderKey">The <c>ModelRouting:Providers</c> key whose endpoint reported this record.</param>
/// <param name="ModelId">
/// The upstream model id exactly as the provider's list named it - matched against a route's
/// <c>ProviderModelId</c>, not its client-facing name. A route configured with an alias the list does not name has
/// no record.
/// </param>
/// <param name="Capabilities">
/// The provider's <c>capabilities</c> object, detached from any <see cref="JsonDocument"/> (via
/// <see cref="JsonElement.Clone"/>) so it can be cached for the process lifetime.
/// </param>
/// <param name="ScannedAtUtc">When the scan that produced this record ran.</param>
public sealed record ModelFeatureSupport(
    string ProviderKey,
    string ModelId,
    JsonElement Capabilities,
    DateTimeOffset ScannedAtUtc)
{
    /// <summary>
    /// Reads the <c>supported</c> flag at <paramref name="path"/> inside <see cref="Capabilities"/>, for example
    /// <c>["thinking", "types", "adaptive"]</c> or <c>["effort"]</c>.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> - unknown - for a missing key, a JSON <c>null</c>, a non-object value or a
    /// <c>supported</c> member that is not a boolean. Callers treat unknown as "send the field as received", so a
    /// record that says nothing about a feature never removes it.
    /// </remarks>
    /// <param name="path">The object keys to walk, outermost first.</param>
    /// <returns>The flag, or <see langword="null"/> when the record does not say.</returns>
    public bool? IsSupported(params ReadOnlySpan<string> path)
    {
        var node = Capabilities;
        foreach (var segment in path)
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(propertyName: segment, value: out node))
                return null;

        return node.ValueKind == JsonValueKind.Object &&
               node.TryGetProperty(propertyName: "supported", value: out var supported) &&
               supported.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? supported.GetBoolean()
            : null;
    }
}
