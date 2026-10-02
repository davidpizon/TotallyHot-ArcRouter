using System.Text.Json;
using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Router.Classification;

/// <summary>
/// Recognizes the vendor-documented markers that tell a request came from a subagent or a narrow side task
/// rather than the main agent (issue #163; the per-harness evidence and the decision to implement or skip
/// each marker live in <c>docs/router/utility-model-routing.md</c>, "Subagent and side-task signals").
/// </summary>
/// <remarks>
/// <para>
/// A pure function over the request headers and parsed body: no I/O, no logging, bounded work (a handful of
/// named headers, never an enumeration of all of them, and one JSON parse capped at
/// <see cref="MaxTurnMetadataLength"/> characters). It never throws - a value it cannot interpret is the
/// same as an absent one.
/// </para>
/// <para>
/// <b>Fail-safe.</b> Anything ambiguous yields <see langword="null"/>, which makes the caller route exactly as
/// it did before #163: a duplicated header, an oversized or non-printable-ASCII value, malformed metadata JSON,
/// a contradictory pair (Claude Code's request class says <c>main</c> while an agent id is present; Codex's
/// <c>subagent_kind</c> without <c>thread_source: "subagent"</c>), or two different harnesses both claiming
/// the request.
/// </para>
/// <para>
/// A signal is a cost preference, not an authorization boundary: a client can send any header. The only effect
/// is a cheaper pick inside the operator's own allowlist and quality gate.
/// </para>
/// </remarks>
public static class SubagentSignalDetector
{
    /// <summary>Claude Code: the id of the subagent that issued the request; present only on subagent requests.</summary>
    internal const string ClaudeCodeAgentIdHeader = "x-claude-code-agent-id";

    /// <summary>Claude Code: the id of the agent that spawned the requesting agent; present only for nested agents.</summary>
    internal const string ClaudeCodeParentAgentIdHeader = "x-claude-code-parent-agent-id";

    /// <summary>Claude Code: the request class hint header (<c>main</c>, <c>subagent</c>, <c>workflow</c>, <c>compaction</c>, <c>auxiliary</c>).</summary>
    internal const string ClaudeCodeRequestClassHeader = "x-claude-code-request-class";

    /// <summary>Codex: the JSON turn-metadata header carrying subagent lineage.</summary>
    internal const string CodexTurnMetadataHeader = "x-codex-turn-metadata";

    /// <summary>The <c>model</c> prefix of the VS Code Copilot background-work aliases.</summary>
    internal const string CopilotUtilityAliasPrefix = "copilot-utility";

    /// <summary>
    /// The longest header value, in characters, the Claude Code checks accept. Agent ids are UUID-like; the
    /// cap only bounds the work a hostile value can cause.
    /// </summary>
    internal const int MaxIdentifierLength = 256;

    /// <summary>
    /// The longest <c>x-codex-turn-metadata</c> value, in characters, that is parsed. Larger values are
    /// treated as no signal rather than parsed.
    /// </summary>
    internal const int MaxTurnMetadataLength = 8 * 1024;

    private const string ClaudeCodeHarness = "claude-code";
    private const string CodexHarness = "codex";
    private const string CopilotHarness = "copilot";

    /// <summary>
    /// Examines the request for a subagent or side-task marker and returns it, or
    /// <see langword="null"/> when there is no enabled, unambiguous one.
    /// </summary>
    /// <param name="headers">The inbound request headers. Looked up by name, case-insensitively.</param>
    /// <param name="requestBody">The parsed top-level request body, read only for the <c>model</c> field.</param>
    /// <param name="options">
    /// The live switches. A disabled <see cref="SubagentBiasOptions.Enabled"/>, or a disabled per-signal flag,
    /// makes the corresponding marker invisible.
    /// </param>
    /// <returns>The detected <see cref="SubagentSignal"/>, or <see langword="null"/>.</returns>
    public static SubagentSignal? Detect(IHeaderDictionary headers, JsonObject requestBody,
        SubagentBiasOptions options)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(requestBody);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled) return null;

        var claudeCode = DetectClaudeCode(headers: headers, options: options);
        var codex = options.CodexTurnMetadata ? DetectCodex(headers) : null;
        var copilot = options.CopilotUtilityAlias ? DetectCopilot(requestBody) : null;

        // Two different harnesses claiming one request is not a coherent request; refuse to guess.
        var found = new[] { claudeCode, codex, copilot }.Where(s => s is not null).ToList();
        return found.Count == 1 ? found[0] : null;
    }

    /// <summary>
    /// Claude Code: <c>x-claude-code-request-class</c> (hint header) and <c>x-claude-code-agent-id</c>. The
    /// class, when present and valid, decides; the agent id stands alone only when no class is sent.
    /// </summary>
    private static SubagentSignal? DetectClaudeCode(IHeaderDictionary headers, SubagentBiasOptions options)
    {
        if (!options.ClaudeCodeAgentId && !options.ClaudeCodeRequestClass) return null;

        if (!TryReadSingle(headers: headers, name: ClaudeCodeAgentIdHeader, value: out var agentId,
                present: out var agentIdPresent) ||
            !TryReadSingle(headers: headers, name: ClaudeCodeParentAgentIdHeader, value: out _,
                present: out var parentPresent) ||
            !TryReadSingle(headers: headers, name: ClaudeCodeRequestClassHeader, value: out var requestClass,
                present: out var classPresent))
            return null;

        // A nested-agent id with no agent id of its own cannot happen in a genuine request.
        if (parentPresent && !agentIdPresent) return null;

        if (classPresent)
        {
            switch (requestClass!.ToLowerInvariant())
            {
                case "subagent":
                    if (options.ClaudeCodeRequestClass)
                        return new SubagentSignal(Harness: ClaudeCodeHarness, Kind: "subagent",
                            Source: ClaudeCodeRequestClassHeader);

                    return options.ClaudeCodeAgentId && agentIdPresent
                        ? new SubagentSignal(Harness: ClaudeCodeHarness, Kind: "subagent",
                            Source: ClaudeCodeAgentIdHeader)
                        : null;

                case "auxiliary":
                    return options.ClaudeCodeRequestClass
                        ? new SubagentSignal(Harness: ClaudeCodeHarness, Kind: "auxiliary",
                            Source: ClaudeCodeRequestClassHeader)
                        : null;

                // A compaction rewrites the whole conversation, so a weaker model risks losing context;
                // a workflow's cost profile is undefined. Neither is a cost-bias signal.
                case "compaction":
                case "workflow":
                    return null;

                // The main conversation never carries an agent id; seeing both is contradictory.
                case "main":
                    return null;

                default:
                    return null;
            }
        }

        return options.ClaudeCodeAgentId && agentIdPresent
            ? new SubagentSignal(Harness: ClaudeCodeHarness, Kind: "subagent", Source: ClaudeCodeAgentIdHeader)
            : null;
    }

    /// <summary>
    /// Codex: <c>x-codex-turn-metadata</c> is a JSON object. A subagent is
    /// <c>thread_source == "subagent"</c> with a <c>parent_thread_id</c> and a <c>subagent_kind</c> of
    /// <c>thread_spawn</c> or <c>memory_consolidation</c>; every other kind (<c>compact</c>, <c>guardian</c>,
    /// <c>review</c>, <c>agent_job:*</c>, <c>other</c>) is not a cost-bias signal.
    /// </summary>
    private static SubagentSignal? DetectCodex(IHeaderDictionary headers)
    {
        if (!TryReadSingle(headers: headers, name: CodexTurnMetadataHeader, value: out var raw,
                present: out var present, maxLength: MaxTurnMetadataLength) || !present)
            return null;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw!);
        }
        catch (JsonException)
        {
            return null;
        }

        if (node is not JsonObject metadata) return null;

        var threadSource = ReadString(node: metadata, key: "thread_source");
        var parentThreadId = ReadString(node: metadata, key: "parent_thread_id");
        var kind = ReadString(node: metadata, key: "subagent_kind");

        if (!string.Equals(a: threadSource, b: "subagent", comparisonType: StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(parentThreadId) || string.IsNullOrEmpty(kind))
            return null;

        return kind switch
        {
            "thread_spawn" => new SubagentSignal(Harness: CodexHarness, Kind: "thread_spawn",
                Source: CodexTurnMetadataHeader),
            "memory_consolidation" => new SubagentSignal(Harness: CodexHarness, Kind: "memory_consolidation",
                Source: CodexTurnMetadataHeader),
            _ => null
        };
    }

    /// <summary>
    /// Copilot: the request's <c>model</c> starts with <c>copilot-utility</c> (exact aliases
    /// <c>copilot-utility</c> and <c>copilot-utility-small</c>, plus any unseen tier, per
    /// <c>docs/router/utility-model-routing.md</c> R1.6), case-insensitively.
    /// </summary>
    private static SubagentSignal? DetectCopilot(JsonObject requestBody)
    {
        var model = ReadString(node: requestBody, key: "model")?.Trim();
        return model is not null && model.Length <= MaxIdentifierLength &&
               model.StartsWith(value: CopilotUtilityAliasPrefix, comparisonType: StringComparison.OrdinalIgnoreCase)
            ? new SubagentSignal(Harness: CopilotHarness, Kind: "utility-alias", Source: "model")
            : null;
    }

    /// <summary>
    /// Reads one header that must appear at most once and hold printable ASCII within
    /// <paramref name="maxLength"/>.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the header is present but unusable (duplicated, blank, too long, or not
    /// printable ASCII) - the caller must treat the request as ambiguous. <see langword="true"/> otherwise,
    /// with <paramref name="present"/> telling absent from a usable value.
    /// </returns>
    private static bool TryReadSingle(IHeaderDictionary headers, string name, out string? value, out bool present,
        int maxLength = MaxIdentifierLength)
    {
        value = null;
        present = false;

        if (!headers.TryGetValue(key: name, value: out var values) || values.Count == 0) return true;

        if (values.Count > 1) return false;

        var text = values[0]?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > maxLength || !IsPrintableAscii(text)) return false;

        value = text;
        present = true;
        return true;
    }

    /// <summary>
    /// Reads <paramref name="key"/> from <paramref name="node"/> as a string, or <see langword="null"/> when it is
    /// missing or not a JSON string.
    /// </summary>
    private static string? ReadString(JsonObject node, string key)
    {
        return node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    /// <summary>
    /// Reports whether every character is printable ASCII (0x20-0x7E). Codex's metadata is JSON, so spaces,
    /// quotes and braces are legitimate; control characters and non-ASCII text are not.
    /// </summary>
    private static bool IsPrintableAscii(string text)
    {
        foreach (var c in text)
            if (c is < ' ' or > '~')
                return false;

        return true;
    }
}
