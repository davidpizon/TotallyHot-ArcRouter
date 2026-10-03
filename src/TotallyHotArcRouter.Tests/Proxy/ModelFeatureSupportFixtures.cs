using System.Text.Json;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Capability records and request bodies shared by the ADR-0022 Amendment 1 tests: the record Anthropic's Models
/// API returned for Claude Haiku 4.5 on 2026-10-02 (pasted by David), a fully capable record, and the request
/// shapes Claude Code 2.1.286 sent under <c>model: auto</c> in the same day's capture.
/// </summary>
internal static class ModelFeatureSupportFixtures
{
    /// <summary>The <c>capabilities</c> object from <c>GET /v1/models/claude-haiku-4-5-20251001</c>, 2026-10-02.</summary>
    public const string Haiku45Capabilities = """
        {"batch":{"supported":true},"citations":{"supported":true},"code_execution":{"supported":false},
         "context_management":{"supported":true,"clear_tool_uses_20250919":{"supported":true},
           "clear_thinking_20251015":{"supported":true},"compact_20260112":{"supported":false}},
         "effort":{"supported":false,"low":{"supported":false},"medium":{"supported":false},
           "high":{"supported":false},"xhigh":{"supported":false},"max":{"supported":false}},
         "image_input":{"supported":true},"pdf_input":{"supported":true},"structured_outputs":{"supported":true},
         "thinking":{"supported":true,"types":{"enabled":{"supported":true},"adaptive":{"supported":false}}}}
        """;

    /// <summary>A record that supports everything Claude Code sends, shaped like Anthropic's documented example.</summary>
    public const string FullCapabilities = """
        {"context_management":{"supported":true,"clear_tool_uses_20250919":{"supported":true},
           "clear_thinking_20251015":{"supported":true},"compact_20260112":{"supported":true}},
         "effort":{"supported":true,"low":{"supported":true},"medium":{"supported":true},
           "high":{"supported":true},"xhigh":{"supported":true},"max":{"supported":true}},
         "thinking":{"supported":true,"types":{"enabled":{"supported":true},"adaptive":{"supported":true}}}}
        """;

    /// <summary>
    /// The body fields an <c>Explore</c> subagent, main or compaction request carried under <c>auto</c>, around a
    /// minimal conversation. The <c>system</c>, <c>tools</c> and <c>messages</c> members let a test prove they are
    /// left untouched.
    /// </summary>
    public const string ExploreBody = """
        {"model":"auto","max_tokens":32000,"stream":true,
         "system":[{"type":"text","text":"You are a file search specialist."}],
         "tools":[{"name":"Glob","input_schema":{"type":"object"}}],
         "messages":[{"role":"user","content":"List the files."}],
         "thinking":{"type":"adaptive","display":"omitted"},
         "output_config":{"effort":"high"},
         "context_management":{"edits":[{"type":"clear_thinking_20251015","keep":"all"}]}}
        """;

    /// <summary>The body fields the <c>auxiliary</c> WebFetch summary carried under <c>auto</c>: effort only.</summary>
    public const string HelperBody = """
        {"model":"auto","max_tokens":32000,"stream":true,
         "messages":[{"role":"user","content":"Summarise this page."}],
         "output_config":{"effort":"high"}}
        """;

    /// <summary>The <c>anthropic-beta</c> value Claude Code 2.1.286 sent on every <c>auto</c> request.</summary>
    public const string CapturedBetaHeader =
        "claude-code-20250219,interleaved-thinking-2025-05-14,thinking-token-count-2026-05-13," +
        "context-management-2025-06-27,prompt-caching-scope-2026-01-05,mid-conversation-system-2026-04-07," +
        "mid-conversation-tool-changes-2026-07-01,effort-2025-11-24";

    /// <summary>Builds a record for <paramref name="modelId"/> from a <c>capabilities</c> JSON object.</summary>
    public static ModelFeatureSupport Record(string capabilitiesJson, string modelId = "claude-haiku-4-5-20251001",
        string providerKey = "anthropic")
    {
        using var document = JsonDocument.Parse(capabilitiesJson);
        return new ModelFeatureSupport(ProviderKey: providerKey, ModelId: modelId,
            Capabilities: document.RootElement.Clone(),
            ScannedAtUtc: new DateTimeOffset(2026, 10, 2, 12, 0, 0, offset: TimeSpan.Zero));
    }

    /// <summary>The Claude Haiku 4.5 record.</summary>
    public static ModelFeatureSupport Haiku45(string modelId = "claude-haiku-4-5-20251001")
    {
        return Record(capabilitiesJson: Haiku45Capabilities, modelId: modelId);
    }

    /// <summary>A fully capable record.</summary>
    public static ModelFeatureSupport Capable(string modelId = "claude-sonnet-5")
    {
        return Record(capabilitiesJson: FullCapabilities, modelId: modelId);
    }
}

/// <summary>An in-memory <see cref="IModelFeatureSupportStore"/> keyed by provider and upstream model id.</summary>
internal sealed class FakeModelFeatureSupportStore : IModelFeatureSupportStore
{
    private readonly Dictionary<ModelCapabilityKey, ModelFeatureSupport> _records = [];

    /// <summary>Gets how many lookups were made, so a test can prove a path never consults the store.</summary>
    public int Lookups { get; private set; }

    /// <inheritdoc/>
    public ModelFeatureSupport? GetModelFeatureSupport(string providerKey, string modelId)
    {
        Lookups++;
        return _records.TryGetValue(key: new ModelCapabilityKey(providerKey: providerKey, modelName: modelId),
            value: out var record)
            ? record
            : null;
    }

    /// <summary>Adds records, as a completed scan would have.</summary>
    public FakeModelFeatureSupportStore With(params ModelFeatureSupport[] records)
    {
        foreach (var record in records)
            _records[new ModelCapabilityKey(providerKey: record.ProviderKey, modelName: record.ModelId)] = record;
        return this;
    }
}
