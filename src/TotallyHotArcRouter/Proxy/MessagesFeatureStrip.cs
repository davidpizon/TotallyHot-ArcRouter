namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// What <see cref="MessagesFeatureStripper"/> removes from one candidate's copy of a native Anthropic Messages
/// request, because that candidate's model reports the features as unsupported
/// (<c>docs/adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md</c> Amendment 1).
/// <para>
/// Computed once, read-only, against the shared parsed body, then applied to a fresh copy only when it is not
/// <see cref="IsEmpty"/>. Also carries what the rest of the forward needs: the <c>anthropic-beta</c> prefixes to
/// drop with their body fields (ADR-0017 Strip rule 1), and the feature names for the log line and the
/// <c>X-ArcRouter-Stripped-Features</c> header (Strip rule 4).
/// </para>
/// </summary>
/// <param name="RemoveThinking">Whether the top-level <c>thinking</c> object goes.</param>
/// <param name="RemoveEffort">
/// Whether <c>output_config.effort</c> goes. <c>output_config</c> itself goes too when nothing else is left in it.
/// </param>
/// <param name="RemovedEditTypes">The <c>context_management.edits</c> entry types that go.</param>
/// <param name="RemoveContextManagement">Whether the whole <c>context_management</c> object goes.</param>
/// <param name="Features">
/// The removed features' names, in a fixed shape: <c>thinking.&lt;type&gt;</c>, <c>output_config.effort</c>,
/// <c>context_management.&lt;strategy&gt;</c> and <c>context_management</c>. The type and strategy halves are keys
/// the model's own capability record named, restricted to letters, digits, <c>_</c>, <c>.</c> and <c>-</c>.
/// </param>
/// <param name="BetaPrefixes">The <c>anthropic-beta</c> value prefixes removed together with the body fields.</param>
public sealed record MessagesFeatureStrip(
    bool RemoveThinking,
    bool RemoveEffort,
    IReadOnlySet<string> RemovedEditTypes,
    bool RemoveContextManagement,
    IReadOnlyList<string> Features,
    IReadOnlyList<string> BetaPrefixes)
{
    /// <summary>The strip that removes nothing - every request that is not stripped.</summary>
    public static MessagesFeatureStrip None { get; } = new(
        RemoveThinking: false,
        RemoveEffort: false,
        RemovedEditTypes: new HashSet<string>(StringComparer.Ordinal),
        RemoveContextManagement: false,
        Features: [],
        BetaPrefixes: []);

    /// <summary>Gets a value indicating whether nothing is removed.</summary>
    public bool IsEmpty => Features.Count == 0;

    /// <summary>
    /// Gets the <c>X-ArcRouter-Stripped-Features</c> header value - the feature names, comma-separated - or
    /// <see langword="null"/> when nothing was removed, in which case the header is omitted.
    /// </summary>
    public string? HeaderValue => IsEmpty ? null : string.Join(separator: ", ", values: Features);
}
