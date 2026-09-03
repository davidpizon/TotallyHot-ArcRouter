using System.Text.Json;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Tests.Models;

/// <summary>
/// Unit tests for <see cref="ModelNameCanonicalizer"/>, covering each normalization stage in isolation,
/// the full pipeline, and the invariant that actually matters downstream: every spelling of one model -
/// the CodeRouterBench dataset's, the provider's, and the operator's configured <c>ModelName</c> -
/// collapses onto a single comparison key, and no two distinct configured models collapse onto the same one.
/// </summary>
public class ModelNameCanonicalizerTests
{
    [Theory]
    [InlineData("  GPT-4o  ", null, "gpt-4o")]
    [InlineData("openai/GPT-4o", "openai", "gpt-4o")]
    [InlineData("meta-llama/Llama-3.1-8B-Instruct", null, "llama-3.1-8b-instruct")]
    [InlineData("gpt-4o", "openai", "gpt-4o")]
    public void NormalizeBase_TrimsLowercases_AndStripsProviderPrefix(string modelId, string? provider, string expected)
    {
        Assert.Equal(expected: expected,
            actual: ModelNameCanonicalizer.NormalizeBase(modelId: modelId, provider: provider));
    }

    // With no provider supplied there is no prefix to match against, so any leading "segment/" is taken
    // as a qualifier - the case a benchmark CSV, which carries no provider column, lands in.
    [Fact]
    public void NormalizeBase_WithoutProvider_StripsAnyLeadingQualifier()
    {
        Assert.Equal(expected: "claude-sonnet-4-6",
            actual: ModelNameCanonicalizer.NormalizeBase("anthropic/claude-sonnet-4-6"));
    }

    // A mismatched provider must not strip anything: silently removing a qualifier the caller did not
    // name would resolve one provider's model under another's key.
    [Fact]
    public void NormalizeBase_WithNonMatchingProvider_LeavesQualifierIntact()
    {
        Assert.Equal(expected: "openai/gpt-4o",
            actual: ModelNameCanonicalizer.NormalizeBase(modelId: "openai/gpt-4o", provider: "anthropic"));
    }

    [Theory]
    [InlineData("claude-sonnet-4-5-20250929", "claude-sonnet-4-5")]
    [InlineData("claude-sonnet-4-5", "claude-sonnet-4-5")]
    // Only an 8-digit run is a dated snapshot; "-2025" is a version component, not a date.
    [InlineData("some-model-2025", "some-model-2025")]
    public void StripSnapshotSuffix_RemovesOnlyDatedSuffixes(string modelId, string expected)
    {
        Assert.Equal(expected: expected, actual: ModelNameCanonicalizer.StripSnapshotSuffix(modelId));
    }

    [Theory]
    [InlineData("gemini-2.5-pro-preview", "gemini-2.5-pro")]
    [InlineData("gpt-4o-LATEST", "gpt-4o")]
    [InlineData("some-model:free", "some-model")]
    [InlineData("gpt-4o", "gpt-4o")]
    public void StripVersionSuffix_RemovesKnownTierSuffixes_CaseInsensitively(string modelId, string expected)
    {
        Assert.Equal(expected: expected, actual: ModelNameCanonicalizer.StripVersionSuffix(modelId));
    }

    [Theory]
    [InlineData("claude-opus-4.6", "claude-opus-4-6")]
    [InlineData("qwen2.5.1-coder-7b-instruct", "qwen2-5-1-coder-7b-instruct")]
    // The dot is only a version separator between two digits; a vendor prefix must survive intact, or a
    // Bedrock ProviderModelId such as "anthropic.claude-3-5-sonnet-20241022-v2:0" would be mangled.
    [InlineData("anthropic.claude-3-5-sonnet", "anthropic.claude-3-5-sonnet")]
    public void UnifyVersionSeparators_RewritesOnlyDigitFlankedDots(string modelId, string expected)
    {
        Assert.Equal(expected: expected, actual: ModelNameCanonicalizer.UnifyVersionSeparators(modelId));
    }

    [Theory]
    [InlineData("claude-opus-4-6", null, "claude-opus-4-6")]
    [InlineData("claude-opus-4.6", null, "claude-opus-4-6")]
    [InlineData("Claude-Opus-4.6", null, "claude-opus-4-6")]
    [InlineData("MiniMax-M2.7", null, "minimax-m2-7")]
    [InlineData("Qwen3-Max", null, "qwen3-max")]
    [InlineData("gpt-5.4", null, "gpt-5-4")]
    [InlineData("anthropic/claude-sonnet-4-6", null, "claude-sonnet-4-6")]
    [InlineData("openai/GPT-4o", "openai", "gpt-4o")]
    public void Canonicalize_CollapsesEverySpellingOntoOneKey(string modelId, string? provider, string expected)
    {
        Assert.Equal(expected: expected,
            actual: ModelNameCanonicalizer.Canonicalize(modelId: modelId, provider: provider));
    }

    /// <summary>
    /// The decision recorded in <c>docs/router/model-identity-canonicalization.md</c>: a dated snapshot
    /// pins one immutable release while its base id is a rolling pointer, so the two must never share an
    /// identity key - collapsing them would silently merge two models' benchmark scores into one cell.
    /// </summary>
    [Fact]
    public void Canonicalize_DoesNotCollapseADatedSnapshotOntoItsBaseModel()
    {
        Assert.NotEqual(
            expected: ModelNameCanonicalizer.Canonicalize("claude-opus-4.6"),
            actual: ModelNameCanonicalizer.Canonicalize("claude-opus-4.6-20250929"));
    }

    /// <summary>
    /// A version/tier suffix likewise denotes a different rolling target than the bare id, so it is never
    /// stripped when forming an identity key. <see cref="ModelNameCanonicalizer.StripVersionSuffix"/>
    /// remains available to the price-resolution ladder, which labels the resulting match approximate.
    /// </summary>
    [Fact]
    public void Canonicalize_DoesNotStripAVersionTierSuffix()
    {
        Assert.NotEqual(
            expected: ModelNameCanonicalizer.Canonicalize("gpt-4o"),
            actual: ModelNameCanonicalizer.Canonicalize("gpt-4o-latest"));
    }

    /// <summary>
    /// The pairing this whole type exists for: the eight ids the CodeRouterBench tables ship and the
    /// <c>ModelName</c> the router is configured with must be the same key, or Phase L's <c>dim_best</c>
    /// lookup misses every Anthropic and mixed-case model.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-4-6", "claude-opus-4.6")]
    [InlineData("claude-sonnet-4-6", "claude-sonnet-4.6")]
    [InlineData("gpt-5.4", "gpt-5.4")]
    [InlineData("glm-5", "glm-5")]
    [InlineData("Qwen3-Max", "qwen3-max")]
    [InlineData("qwen3.5-plus", "qwen3.5-plus")]
    [InlineData("kimi-k2.5", "kimi-k2.5")]
    [InlineData("MiniMax-M2.7", "minimax-m2.7")]
    public void Canonicalize_MapsDatasetIdAndConfiguredModelName_ToTheSameKey(string datasetId,
        string configuredModelName)
    {
        Assert.Equal(
            expected: ModelNameCanonicalizer.Canonicalize(configuredModelName),
            actual: ModelNameCanonicalizer.Canonicalize(datasetId));
    }

    // An id no rule recognizes is still normalized but never guessed at - the "leave it unresolved rather
    // than approximately mapped" rule from docs/router/d3-alias-resolution.md.
    [Fact]
    public void Canonicalize_UnknownModel_IsNormalizedButNotAliased()
    {
        Assert.Equal(expected: "some-unknown-model", actual: ModelNameCanonicalizer.Canonicalize("Some-Unknown-Model"));
    }

    /// <summary>
    /// Guards the one way this design can fail silently: if two distinct configured <c>ModelName</c>s ever
    /// canonicalized to the same key, the matrix would merge two different models' scores into one cell
    /// and every lookup would return a blend. Reads the real <c>appsettings.json</c> so a future config
    /// edit that introduces such a pair fails here rather than in production.
    /// </summary>
    [Fact]
    public void EveryConfiguredModelName_CanonicalizesToADistinctKey()
    {
        var modelNames = ReadConfiguredModelNames();

        Assert.NotEmpty(modelNames);

        var collisions = modelNames
            .GroupBy(keySelector: name => ModelNameCanonicalizer.Canonicalize(name), comparer: StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} <- {string.Join(separator: ", ", values: group)}")
            .ToList();

        Assert.True(condition: collisions.Count == 0,
            userMessage:
            $"Configured ModelNames collide on canonicalization: {string.Join(separator: "; ", values: collisions)}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Canonicalize_BlankInput_Throws(string? modelId)
    {
        // ThrowsAny, because a null argument surfaces as ArgumentNullException and empty/whitespace as
        // ArgumentException - both from ArgumentException.ThrowIfNullOrWhiteSpace.
        Assert.ThrowsAny<ArgumentException>(() => ModelNameCanonicalizer.Canonicalize(modelId!));
    }

    /// <summary>Reads <c>ModelRouting:ModelList[].ModelName</c> straight out of the shipped configuration file.</summary>
    private static List<string> ReadConfiguredModelNames()
    {
        var appSettingsPath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "TotallyHotArcRouter", "appsettings.json");

        // appsettings.json carries "//" comments explaining the routing entries, which strict JSON rejects.
        var options = new JsonDocumentOptions
            { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var document = JsonDocument.Parse(json: File.ReadAllText(appSettingsPath), options: options);

        return
        [
            .. document.RootElement
                .GetProperty("ModelRouting")
                .GetProperty("ModelList")
                .EnumerateArray()
                .Select(entry => entry.GetProperty("ModelName").GetString()!)
        ];
    }
}