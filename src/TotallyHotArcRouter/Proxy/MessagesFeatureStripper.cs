using System.Text;
using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Decides and applies which request features a router-chosen candidate's copy of a native Anthropic Messages
/// body drops, because the candidate model's own capability record reports them unsupported
/// (<c>docs/adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md</c> Amendment 1).
/// </summary>
/// <remarks>
/// <para>
/// Claude Code treats an unrecognized model id such as <c>auto</c> as a current Claude model and sends adaptive
/// thinking, <c>output_config.effort</c> and <c>context_management</c>. A model that rejects one of them answers
/// 400, and Claude Code then turns the feature off for the rest of the conversation - for effort, for every later
/// <c>auto</c> request. Removing the feature from the copy sent to that one model avoids both.
/// </para>
/// <para>
/// No rule here names a model, family or version, and none depends on which client sent the request. Each check
/// asks the record about the exact name the request carries - the thinking type, the effort level, the
/// context-management strategy - so a record that does not mention a name leaves that field alone. Beta values are
/// matched by feature prefix, so a newly dated value needs no change here.
/// </para>
/// <para>
/// Pure: <see cref="Plan"/> only reads, and <see cref="Apply"/> mutates only the body it is given.
/// </para>
/// </remarks>
internal static class MessagesFeatureStripper
{
    /// <summary>The <c>anthropic-beta</c> prefix paired with <c>output_config.effort</c>.</summary>
    internal const string EffortBetaPrefix = "effort-";

    /// <summary>The <c>anthropic-beta</c> prefix paired with <c>context_management</c>.</summary>
    internal const string ContextManagementBetaPrefix = "context-management-";

    /// <summary>
    /// The context-management strategy family that manages thinking blocks. Such an edit is removed whenever
    /// <c>thinking</c> is, because it has nothing to manage then, and whether a model accepts it without thinking
    /// is undocumented.
    /// </summary>
    private const string ClearThinkingStrategyPrefix = "clear_thinking_";

    /// <summary>The longest feature-name half copied into a log line or header.</summary>
    private const int MaxFeatureNameLength = 64;

    /// <summary>
    /// Works out what <paramref name="body"/>'s copy for <paramref name="support"/>'s model must drop, without
    /// changing <paramref name="body"/>.
    /// </summary>
    /// <param name="body">The parsed request body, shared across candidates and therefore only read.</param>
    /// <param name="support">The candidate model's capability record.</param>
    /// <returns>The strip, or <see cref="MessagesFeatureStrip.None"/> when the record rejects nothing the body sends.</returns>
    public static MessagesFeatureStrip Plan(JsonObject body, ModelFeatureSupport support)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(support);

        var features = new List<string>();
        var betaPrefixes = new List<string>();

        var removeThinking = false;
        if (body["thinking"] is JsonObject thinking && ReadString(thinking["type"]) is { } thinkingType &&
            (support.IsSupported("thinking") == false ||
             support.IsSupported("thinking", "types", thinkingType) == false))
        {
            removeThinking = true;
            features.Add($"thinking.{SanitizeName(thinkingType)}");
        }

        var removeEffort = false;
        if (body["output_config"] is JsonObject outputConfig && ReadString(outputConfig["effort"]) is { } level &&
            (support.IsSupported("effort") == false || support.IsSupported("effort", level) == false))
        {
            removeEffort = true;
            features.Add("output_config.effort");
            betaPrefixes.Add(EffortBetaPrefix);
        }

        var removedEditTypes = new HashSet<string>(StringComparer.Ordinal);
        var removeContextManagement = false;
        if (body["context_management"] is JsonObject contextManagement)
        {
            if (support.IsSupported("context_management") == false)
            {
                removeContextManagement = true;
            }
            else if (contextManagement["edits"] is JsonArray edits)
            {
                var keptEdits = 0;
                foreach (var edit in edits)
                {
                    var strategy = edit is JsonObject editObject ? ReadString(editObject["type"]) : null;
                    var drop = strategy is not null &&
                               (support.IsSupported("context_management", strategy) == false ||
                                (removeThinking &&
                                 strategy.StartsWith(value: ClearThinkingStrategyPrefix,
                                     comparisonType: StringComparison.Ordinal)));

                    if (drop) removedEditTypes.Add(strategy!);
                    else keptEdits++;
                }

                foreach (var strategy in removedEditTypes) features.Add($"context_management.{SanitizeName(strategy)}");

                // The object goes only when the edits were all it held. Any other member is left in place with
                // whatever edits remain, rather than removing configuration this record said nothing about.
                removeContextManagement = removedEditTypes.Count > 0 && keptEdits == 0 && contextManagement.Count == 1;
            }

            if (removeContextManagement)
            {
                features.Add("context_management");
                betaPrefixes.Add(ContextManagementBetaPrefix);
            }
        }

        return features.Count == 0
            ? MessagesFeatureStrip.None
            : new MessagesFeatureStrip(
                RemoveThinking: removeThinking,
                RemoveEffort: removeEffort,
                RemovedEditTypes: removedEditTypes,
                RemoveContextManagement: removeContextManagement,
                Features: features,
                BetaPrefixes: betaPrefixes);
    }

    /// <summary>
    /// Removes what <paramref name="strip"/> names from <paramref name="body"/>. Only ever called on a candidate's
    /// own copy (ADR-0017 Strip rule 2), never on the shared parsed body. Earlier messages are never touched.
    /// </summary>
    /// <param name="body">The candidate's own copy of the request body.</param>
    /// <param name="strip">The strip <see cref="Plan"/> produced for this candidate.</param>
    public static void Apply(JsonObject body, MessagesFeatureStrip strip)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(strip);

        if (strip.RemoveThinking) body.Remove("thinking");

        if (strip.RemoveEffort && body["output_config"] is JsonObject outputConfig)
        {
            outputConfig.Remove("effort");
            if (outputConfig.Count == 0) body.Remove("output_config");
        }

        if (strip.RemoveContextManagement)
        {
            body.Remove("context_management");
        }
        else if (strip.RemovedEditTypes.Count > 0 &&
                 body["context_management"] is JsonObject contextManagement &&
                 contextManagement["edits"] is JsonArray edits)
        {
            for (var i = edits.Count - 1; i >= 0; i--)
                if (edits[i] is JsonObject edit && ReadString(edit["type"]) is { } strategy &&
                    strip.RemovedEditTypes.Contains(strategy))
                    edits.RemoveAt(i);
        }
    }

    /// <summary>
    /// Removes every comma-separated value in an <c>anthropic-beta</c> header that starts with one of
    /// <paramref name="prefixes"/>, keeping the rest in order and unchanged.
    /// </summary>
    /// <param name="headerValue">One <c>anthropic-beta</c> header value as the client sent it.</param>
    /// <param name="prefixes">The beta prefixes whose body fields were removed.</param>
    /// <returns>The remaining values joined by commas, or <see langword="null"/> when none is left.</returns>
    public static string? FilterBetaHeader(string headerValue, IReadOnlyList<string> prefixes)
    {
        ArgumentNullException.ThrowIfNull(headerValue);
        ArgumentNullException.ThrowIfNull(prefixes);

        var kept = headerValue
            .Split(separator: ',', options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !prefixes.Any(prefix =>
                value.StartsWith(value: prefix, comparisonType: StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return kept.Count == 0 ? null : string.Join(separator: ',', values: kept);
    }

    /// <summary>Reads a non-blank JSON string value, or <see langword="null"/> for anything else.</summary>
    private static string? ReadString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
    }

    /// <summary>
    /// Restricts a name taken from the request to letters, digits, <c>_</c>, <c>.</c> and <c>-</c>, bounded in
    /// length, before it reaches a log line or response header. The name has already matched a key in the
    /// model's capability record, so this is a backstop, not the main defense.
    /// </summary>
    private static string SanitizeName(string name)
    {
        var builder = new StringBuilder(capacity: Math.Min(val1: name.Length, val2: MaxFeatureNameLength));
        foreach (var character in name.AsSpan(0, length: Math.Min(val1: name.Length, val2: MaxFeatureNameLength)))
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-' ? character : '_');

        return builder.ToString();
    }
}
