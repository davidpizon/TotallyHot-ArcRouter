using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

/// <summary>
/// An in-memory <see cref="IToolCallCapabilityStore"/> that also records every write, so a test can assert
/// what a response taught the router about its model without standing up SQLite.
/// </summary>
/// <remarks>
/// <c>ToolCallCapabilityStoreTests</c> covers the real store, including the confidence gate this fake
/// deliberately does not reproduce - here every write is accepted, so a test asserting "an operator pin is
/// not overwritten" must seed the pin and assert the *decision not to arm*, which is the part that lives in
/// <see cref="ToolCallNormalizerFactory"/>.
/// </remarks>
internal sealed class FakeToolCallCapabilityStore : IToolCallCapabilityStore, IModelContextWindowStore
{
    private readonly Dictionary<ModelCapabilityKey, ModelToolCapability> _rows = new();
    private readonly Dictionary<string, ProviderEndpointCapabilities> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ModelCapabilityKey, ModelContextWindow> _contextWindows = new();

    /// <summary>Gets every capability written through <see cref="TryRecordModelCapability"/>, in order.</summary>
    public List<ModelToolCapability> Recorded { get; } = [];

    /// <summary>Pre-populates a classification, as a prior scan or an operator pin would have.</summary>
    public FakeToolCallCapabilityStore Seed(ModelToolCapability capability)
    {
        _rows[new ModelCapabilityKey(capability.ProviderKey, capability.ModelName)] = capability;
        return this;
    }

    /// <summary>
    /// Pre-populates a provider's endpoint flavors, as an endpoint scan would have. Defaults to the
    /// local-runtime shape, since <c>JsonSchemaResponseFormat</c> is the flag most tests care about here.
    /// </summary>
    public FakeToolCallCapabilityStore SeedProvider(string providerKey, bool jsonSchemaResponseFormat)
    {
        _providers[providerKey] = new ProviderEndpointCapabilities(
            providerKey,
            OpenAiCompatible: true,
            LmStudioNative: jsonSchemaResponseFormat,
            OllamaNative: false,
            AnthropicCompatible: false,
            JsonSchemaResponseFormat: jsonSchemaResponseFormat,
            ScannedAtUtc: DateTimeOffset.UtcNow);
        return this;
    }

    /// <summary>
    /// Pre-populates a probed context window, as an endpoint scan would have. Mirrors the real store, where
    /// a window is written by the scan path independently of any dialect row - so a test can seed one
    /// without the other, which is exactly the case /api/show has to describe correctly.
    /// </summary>
    public FakeToolCallCapabilityStore SeedContextWindow(
        string providerKey, string modelName, int contextLength, string? architecture = null)
    {
        _contextWindows[new ModelCapabilityKey(providerKey, modelName)] =
            new ModelContextWindow(providerKey, modelName, contextLength, architecture, "seeded by test", DateTimeOffset.UtcNow);
        return this;
    }

    /// <inheritdoc />
    public ModelContextWindow? GetModelContextWindow(string providerKey, string modelName) =>
        _contextWindows.TryGetValue(new ModelCapabilityKey(providerKey, modelName), out var window) ? window : null;

    /// <inheritdoc />
    public ModelToolCapability? GetModelCapability(string providerKey, string modelName) =>
        _rows.TryGetValue(new ModelCapabilityKey(providerKey, modelName), out var capability) ? capability : null;

    /// <inheritdoc />
    public ProviderEndpointCapabilities? GetProviderCapabilities(string providerKey) =>
        _providers.TryGetValue(providerKey, out var capabilities) ? capabilities : null;

    /// <inheritdoc />
    public bool TryRecordModelCapability(ModelToolCapability capability)
    {
        Recorded.Add(capability);
        Seed(capability);
        return true;
    }
}

