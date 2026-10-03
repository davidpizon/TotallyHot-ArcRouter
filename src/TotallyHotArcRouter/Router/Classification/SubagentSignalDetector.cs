using System.Text.Json;
using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Router.Classification;

/// <summary>
/// Recognizes the vendor-documented markers that tell a request came from a subagent or a helper task
/// rather than the main agent, and assigns each one its <see cref="SubagentRouteClass"/> (issue #163). The
/// per-harness evidence lives in <c>docs/router/utility-model-routing.md</c> ("Subagent and side-task
/// signals"); the routing evidence lives in <c>docs/research/subagent-and-helper-routing-evidence.md</c> and
/// <c>docs/adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md</c>.
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
/// a contradictory combination (Claude Code's request class says <c>main</c> while an agent id is present, or an
/// agent type arrives without an agent id; Codex's <c>subagent_kind</c> without <c>thread_source: "subagent"</c>),
/// or two different harnesses both claiming the request.
/// </para>
/// <para>
/// Route classes follow vendor defaults: Claude Code <c>auxiliary</c> requests and Copilot utility aliases are
/// <see cref="SubagentRouteClass.Helper"/>; Claude Code's <c>Explore</c> and <c>claude-code-guide</c> subagents are
/// <see cref="SubagentRouteClass.LightSubagent"/>; every other subagent, and every Codex signal, is
/// <see cref="SubagentRouteClass.Normal"/>.
/// </para>
/// <para>
/// A signal is a cost preference, not an authorization boundary: a client can send any header. The only effect
/// is a cheaper pick inside the operator's own allowlist and quality gate.
/// </para>
/// </remarks>
public static class SubagentSignalDetector
{
    /// <summary>Claude Code: the id of the subagent that issued the request; present only on subagent requests.</summary>
    private const string ClaudeCodeAgentIdHeader = "x-claude-code-agent-id";

    /// <summary>Claude Code: the id of the agent that spawned the requesting agent; present only for nested agents.</summary>
    private const string ClaudeCodeParentAgentIdHeader = "x-claude-code-parent-agent-id";

    /// <summary>Claude Code: the request class hint header (<c>main</c>, <c>subagent</c>, <c>workflow</c>, <c>compaction</c>, <c>auxiliary</c>).</summary>
    private const string ClaudeCodeRequestClassHeader = "x-claude-code-request-class";

    /// <summary>
    /// Claude Code: the agent type hint header, sent only on a subagent's own turns (a built-in type name such as
    /// <c>Explore</c>, or <c>custom</c>, <c>teammate</c>, <c>fork</c>; never a user-chosen agent name).
    /// </summary>
    private const string ClaudeCodeAgentTypeHeader = "x-claude-code-agent-type";

    /// <summary>Codex: the JSON turn-metadata header carrying subagent lineage.</summary>
    private const string CodexTurnMetadataHeader = "x-codex-turn-metadata";

    /// <summary>The <c>model</c> prefix of the VS Code Copilot background-work aliases.</summary>
    private const string CopilotUtilityAliasPrefix = "copilot-utility";

    /// <summary>
    /// The longest header value, in characters, the Claude Code checks accept. Agent ids are UUID-like; the
    /// cap only bounds the work a hostile value can cause.
    /// </summary>
    private const int MaxIdentifierLength = 256;

    /// <summary>
    /// The longest <c>x-codex-turn-metadata</c> value, in characters, that is parsed. Larger values are
    /// treated as no signal rather than parsed.
    /// </summary>
    private const int MaxTurnMetadataLength = 8 * 1024;

    private const string ClaudeCodeHarness = "claude-code";
    private const string CodexHarness = "codex";
    private const string CopilotHarness = "copilot";

    /// <summary>The <see cref="SubagentSignal.Kind"/> for a Claude Code subagent whose type is unknown or not sent.</summary>
    private const string UnknownSubagentKind = "subagent";

    /// <summary>
    /// Claude Code's documented agent types, mapped to the lowercase <see cref="SubagentSignal.Kind"/> they are
    /// reported as. Anything else is reported as <see cref="UnknownSubagentKind"/>, so a client-chosen value never
    /// reaches a log line or the wire.
    /// </summary>
    private static readonly Dictionary<string, string> KnownClaudeCodeAgentTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Explore"] = "explore",
            ["Plan"] = "plan",
            ["general-purpose"] = "general-purpose",
            ["statusline-setup"] = "statusline-setup",
            ["claude-code-guide"] = "claude-code-guide",
            ["custom"] = "custom",
            ["teammate"] = "teammate",
            ["fork"] = "fork"
        };

    /// <summary>
    /// Examines the request for a subagent or helper marker and returns it, or
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
    /// Reports whether <paramref name="modelName"/> is one of VS Code Copilot's background-work aliases: it starts
    /// with <c>copilot-utility</c> (exact aliases <c>copilot-utility</c> and <c>copilot-utility-small</c>, plus any
    /// unseen tier, per <c>docs/router/utility-model-routing.md</c> R1.6), case-insensitively, after trimming.
    /// Such a name both marks a helper and delegates the model choice to the router.
    /// </summary>
    /// <param name="modelName">The request's <c>model</c> value.</param>
    /// <returns><see langword="true"/> for a Copilot utility alias.</returns>
    public static bool IsCopilotUtilityAlias(string? modelName)
    {
        var model = modelName?.Trim();
        return model is not null && model.Length <= MaxIdentifierLength &&
               model.StartsWith(value: CopilotUtilityAliasPrefix, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Claude Code: the hint headers (<c>x-claude-code-request-class</c>, <c>x-claude-code-agent-type</c>) when
    /// enabled, otherwise the agent id alone. The request class, when present and valid, decides; the agent type
    /// then names the subagent; the agent id stands alone only when no hint header is used.
    /// </summary>
    private static SubagentSignal? DetectClaudeCode(IHeaderDictionary headers, SubagentBiasOptions options)
    {
        if (options is { ClaudeCodeAgentId: false, ClaudeCodeHintHeaders: false }) return null;

        if (!TryReadSingle(headers: headers, name: ClaudeCodeAgentIdHeader, value: out _,
                present: out var agentIdPresent) ||
            !TryReadSingle(headers: headers, name: ClaudeCodeParentAgentIdHeader, value: out _,
                present: out var parentPresent))
            return null;

        // The hint headers are read only when they are enabled. A disabled hint is invisible: a duplicated or
        // malformed one, or a contradictory class/type pair, must not suppress the agent-id signal below.
        string? requestClass = null, agentType = null;
        bool classPresent = false, typePresent = false;
        if (options.ClaudeCodeHintHeaders &&
            (!TryReadSingle(headers: headers, name: ClaudeCodeRequestClassHeader, value: out requestClass,
                 present: out classPresent) ||
             !TryReadSingle(headers: headers, name: ClaudeCodeAgentTypeHeader, value: out agentType,
                 present: out typePresent)))
            return null;

        // A nested-agent id, or an agent type, only ever rides on a request from a spawned agent - which always
        // carries its own agent id. Without one, the combination cannot come from a genuine request.
        if ((parentPresent || typePresent) && !agentIdPresent) return null;

        if (classPresent)
            return requestClass!.ToLowerInvariant() switch
            {
                "subagent" => ClaudeCodeSubagent(agentType: agentType,
                    source: typePresent ? ClaudeCodeAgentTypeHeader : ClaudeCodeRequestClassHeader),

                // A type is only sent on a subagent's own turns; a typed auxiliary request is contradictory.
                "auxiliary" => typePresent
                    ? null
                    : new SubagentSignal(Harness: ClaudeCodeHarness, Kind: "auxiliary",
                        Source: ClaudeCodeRequestClassHeader, RouteClass: SubagentRouteClass.Helper),

                // `main` never carries an agent id or type. `compaction` rewrites the whole conversation and needs
                // fidelity; `workflow` has no defined cost profile; an unknown class is uninterpretable. None of
                // them is a routing signal.
                _ => null
            };

        if (typePresent)
            return ClaudeCodeSubagent(agentType: agentType, source: ClaudeCodeAgentTypeHeader);

        return options.ClaudeCodeAgentId && agentIdPresent
            ? ClaudeCodeSubagent(agentType: null, source: ClaudeCodeAgentIdHeader)
            : null;
    }

    /// <summary>
    /// Builds a Claude Code subagent signal: <c>Explore</c> and <c>claude-code-guide</c> are light subagents (the
    /// subagents Anthropic itself runs, or suggests running, on Haiku); every other or unknown type routes normally.
    /// </summary>
    private static SubagentSignal ClaudeCodeSubagent(string? agentType, string source)
    {
        var kind = agentType is not null && KnownClaudeCodeAgentTypes.TryGetValue(key: agentType, value: out var known)
            ? known
            : UnknownSubagentKind;
        var routeClass = kind is "explore" or "claude-code-guide"
            ? SubagentRouteClass.LightSubagent
            : SubagentRouteClass.Normal;

        return new SubagentSignal(Harness: ClaudeCodeHarness, Kind: kind, Source: source, RouteClass: routeClass);
    }

    /// <summary>
    /// Codex: <c>x-codex-turn-metadata</c> is a JSON object. A subagent is
    /// <c>thread_source == "subagent"</c> with a <c>parent_thread_id</c> and a <c>subagent_kind</c> of
    /// <c>thread_spawn</c> or <c>memory_consolidation</c>; every other kind (<c>compact</c>, <c>guardian</c>,
    /// <c>review</c>, <c>agent_job:*</c>, <c>other</c>) is not a signal. Both recognized kinds route normally:
    /// Codex sends no agent type, so narrow work cannot be told from open-ended work, and consolidation's output
    /// persists into later sessions.
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
                Source: CodexTurnMetadataHeader, RouteClass: SubagentRouteClass.Normal),
            "memory_consolidation" => new SubagentSignal(Harness: CodexHarness, Kind: "memory_consolidation",
                Source: CodexTurnMetadataHeader, RouteClass: SubagentRouteClass.Normal),
            _ => null
        };
    }

    /// <summary>Copilot: the request's <c>model</c> is a <see cref="IsCopilotUtilityAlias">utility alias</see>.</summary>
    private static SubagentSignal? DetectCopilot(JsonObject requestBody)
    {
        return IsCopilotUtilityAlias(ReadString(node: requestBody, key: "model"))
            ? new SubagentSignal(Harness: CopilotHarness, Kind: "utility-alias", Source: "model",
                RouteClass: SubagentRouteClass.Helper)
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
