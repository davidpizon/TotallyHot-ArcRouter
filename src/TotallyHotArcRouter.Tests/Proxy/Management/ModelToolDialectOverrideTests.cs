using Moq;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers the operator override for a model's tool-call dialect - TotallyHot.ArcRouter's equivalent of LiteLLM's
/// <c>register_model(..., supports_function_calling=…)</c>.
/// <para>
/// The reason it exists is a failure mode automatic detection cannot escape on its own. A model that emits
/// a real <c>tool_calls</c> field on only some replies is recorded <c>openai-native</c> at
/// <see cref="DetectionConfidence.Observed"/> the first time it happens to succeed; performance rule 2 then
/// stops arming it, so no later reply is inspected and no contrary evidence is ever collected. The
/// classification is self-sealing, and every subsequent free-text reply reaches the client as raw JSON.
/// Observed live on <c>qwen2.5-coder-7b-instruct-ghidra-v2</c>. A human has to be able to say otherwise.
/// </para>
/// </summary>
public sealed class ModelToolDialectOverrideTests : IDisposable
{
    private const string Provider = "lmstudio";
    private const string Model = "qwen2.5-coder-7b-instruct-ghidra-v2";

    private readonly TempDatabase _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void SettingADialect_PinsItAtOperatorConfidence()
    {
        var capabilities = _temp.CreateToolCallCapabilityStore();
        var facade = Facade(capabilities);

        var result = facade.SetModelToolDialect(key: Provider, modelName: Model,
            request: new ModelToolDialectWriteRequest("constrained"));

        Assert.True(result.Success);

        var stored = capabilities.GetModelCapability(providerKey: Provider, modelName: Model);
        Assert.Equal(expected: "constrained", actual: stored!.Dialect);
        Assert.Equal(expected: DetectionConfidence.Operator, actual: stored.Confidence);
    }

    [Fact]
    public void APin_OutranksAnExistingObservedClassification()
    {
        // The self-sealing misclassification this whole surface exists to undo.
        var capabilities = _temp.CreateToolCallCapabilityStore();
        capabilities.TryRecordModelCapability(new ModelToolCapability(
            ProviderKey: Provider, ModelName: Model, Dialect: "openai-native", Confidence: DetectionConfidence.Observed,
            Evidence: "Response carried native tool_calls."));

        Facade(capabilities).SetModelToolDialect(key: Provider, modelName: Model,
            request: new ModelToolDialectWriteRequest("constrained"));

        Assert.Equal(expected: "constrained",
            actual: capabilities.GetModelCapability(providerKey: Provider, modelName: Model)!.Dialect);
    }

    [Fact]
    public void APin_IsNotOverwrittenByALaterAutomaticObservation()
    {
        // The point of Operator confidence: the same lucky native reply that caused the problem must not be
        // able to undo the operator's correction of it.
        var capabilities = _temp.CreateToolCallCapabilityStore();
        Facade(capabilities).SetModelToolDialect(key: Provider, modelName: Model,
            request: new ModelToolDialectWriteRequest("constrained"));

        var accepted = capabilities.TryRecordModelCapability(new ModelToolCapability(
            ProviderKey: Provider, ModelName: Model, Dialect: "openai-native", Confidence: DetectionConfidence.Observed,
            Evidence: "Response carried native tool_calls."));

        Assert.False(accepted);
        Assert.Equal(expected: "constrained",
            actual: capabilities.GetModelCapability(providerKey: Provider, modelName: Model)!.Dialect);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ClearingThePin_DeletesTheRow_SoDetectionStartsOver(string? dialect)
    {
        // Deleted rather than downgraded: ToolCallNormalizerFactory reads a *missing* row as "forward
        // natively and classify from the first real response", which is exactly the fresh start being asked
        // for. A low-confidence row would still be a row.
        var capabilities = _temp.CreateToolCallCapabilityStore();
        var facade = Facade(capabilities);
        facade.SetModelToolDialect(key: Provider, modelName: Model,
            request: new ModelToolDialectWriteRequest("constrained"));

        var result = facade.SetModelToolDialect(key: Provider, modelName: Model,
            request: new ModelToolDialectWriteRequest(dialect));

        Assert.True(result.Success);
        Assert.Null(capabilities.GetModelCapability(providerKey: Provider, modelName: Model));
    }

    [Fact]
    public void AnUnknownDialect_IsRejectedRatherThanStored()
    {
        // A typo must not silently disable tool calling while the UI shows the pin as applied. Distinct from
        // how an unknown name read *back from disk* is treated - that degrades to "not scanned", which is
        // right for a row a newer build wrote.
        var capabilities = _temp.CreateToolCallCapabilityStore();

        var result = Facade(capabilities).SetModelToolDialect(key: Provider, modelName: Model,
            request: new ModelToolDialectWriteRequest("hermes-ish"));

        Assert.False(result.Success);
        Assert.Equal(expected: ManagementErrorType.InvalidRequest, actual: result.ErrorType);
        Assert.Null(capabilities.GetModelCapability(providerKey: Provider, modelName: Model));
    }

    [Fact]
    public void AnUnknownProviderOrModel_IsNotFound()
    {
        var capabilities = _temp.CreateToolCallCapabilityStore();
        var facade = Facade(capabilities);

        Assert.Equal(
            expected: ManagementErrorType.NotFound,
            actual: facade.SetModelToolDialect(key: "nope", modelName: Model,
                request: new ModelToolDialectWriteRequest("constrained")).ErrorType);

        Assert.Equal(
            expected: ManagementErrorType.NotFound,
            actual: facade.SetModelToolDialect(key: Provider, modelName: "no-such-model",
                request: new ModelToolDialectWriteRequest("constrained")).ErrorType);
    }

    [Fact]
    public void WithNoCapabilityStore_TheOverrideReportsItselfUnavailable()
    {
        var result = Facade(capabilityStore: null)
            .SetModelToolDialect(key: Provider, modelName: Model,
                request: new ModelToolDialectWriteRequest("constrained"));

        Assert.Equal(expected: ManagementErrorType.Unavailable, actual: result.ErrorType);
    }

    [Fact]
    public void ThePinnedDialect_IsSurfacedThroughListProviders()
    {
        // BuildProvidersResponse is what the Governance dropdown renders from, so a pin that is stored but
        // invisible there would read to the operator as though nothing happened.
        var capabilities = _temp.CreateToolCallCapabilityStore();
        var facade = Facade(capabilities);

        facade.SetModelToolDialect(key: Provider, modelName: Model,
            request: new ModelToolDialectWriteRequest("constrained"));

        var model = Assert.Single(Assert.Single(facade.ListProviders().Providers).Models);
        Assert.Equal(expected: "constrained", actual: model.Dialect);
        Assert.Equal(expected: nameof(DetectionConfidence.Operator), actual: model.Confidence, true);
    }

    private ManagementFacade Facade(ToolCallCapabilityStore? capabilityStore)
    {
        var store = new InMemoryProviderConfigStore(new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                [Provider] = new() { BaseUrl = "http://localhost:1234/v1" }
            },
            ModelList = [new ModelRouteEntry { ModelName = Model, Provider = Provider, ProviderModelId = Model }]
        });

        return new ManagementFacade(
            store: store, environment: Mock.Of<IEnvironmentVariableProvider>(), httpClient: new HttpClient(),
            dependencies: new ManagementFacadeDependencies { CapabilityStore = capabilityStore });
    }
}