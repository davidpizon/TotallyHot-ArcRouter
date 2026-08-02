using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Tests.PriceCatalog;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers the operator override for a model's tool-call dialect - TotallyHot.ArcRouter's equivalent of LiteLLM's
/// <c>register_model(..., supports_function_calling=…)</c>.
///
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

    [Fact]
    public void SettingADialect_PinsItAtOperatorConfidence()
    {
        var capabilities = _temp.CreateToolCallCapabilityStore();
        var facade = Facade(capabilities);

        var result = facade.SetModelToolDialect(Provider, Model, new ModelToolDialectWriteRequest("constrained"));

        Assert.True(result.Success);

        var stored = capabilities.GetModelCapability(Provider, Model);
        Assert.Equal("constrained", stored!.Dialect);
        Assert.Equal(DetectionConfidence.Operator, stored.Confidence);
    }

    [Fact]
    public void APin_OutranksAnExistingObservedClassification()
    {
        // The self-sealing misclassification this whole surface exists to undo.
        var capabilities = _temp.CreateToolCallCapabilityStore();
        capabilities.TryRecordModelCapability(new ModelToolCapability(
            Provider, Model, "openai-native", DetectionConfidence.Observed, "Response carried native tool_calls."));

        Facade(capabilities).SetModelToolDialect(Provider, Model, new ModelToolDialectWriteRequest("constrained"));

        Assert.Equal("constrained", capabilities.GetModelCapability(Provider, Model)!.Dialect);
    }

    [Fact]
    public void APin_IsNotOverwrittenByALaterAutomaticObservation()
    {
        // The point of Operator confidence: the same lucky native reply that caused the problem must not be
        // able to undo the operator's correction of it.
        var capabilities = _temp.CreateToolCallCapabilityStore();
        Facade(capabilities).SetModelToolDialect(Provider, Model, new ModelToolDialectWriteRequest("constrained"));

        var accepted = capabilities.TryRecordModelCapability(new ModelToolCapability(
            Provider, Model, "openai-native", DetectionConfidence.Observed, "Response carried native tool_calls."));

        Assert.False(accepted);
        Assert.Equal("constrained", capabilities.GetModelCapability(Provider, Model)!.Dialect);
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
        facade.SetModelToolDialect(Provider, Model, new ModelToolDialectWriteRequest("constrained"));

        var result = facade.SetModelToolDialect(Provider, Model, new ModelToolDialectWriteRequest(dialect));

        Assert.True(result.Success);
        Assert.Null(capabilities.GetModelCapability(Provider, Model));
    }

    [Fact]
    public void AnUnknownDialect_IsRejectedRatherThanStored()
    {
        // A typo must not silently disable tool calling while the UI shows the pin as applied. Distinct from
        // how an unknown name read *back from disk* is treated - that degrades to "not scanned", which is
        // right for a row a newer build wrote.
        var capabilities = _temp.CreateToolCallCapabilityStore();

        var result = Facade(capabilities).SetModelToolDialect(Provider, Model, new ModelToolDialectWriteRequest("hermes-ish"));

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.InvalidRequest, result.ErrorType);
        Assert.Null(capabilities.GetModelCapability(Provider, Model));
    }

    [Fact]
    public void AnUnknownProviderOrModel_IsNotFound()
    {
        var capabilities = _temp.CreateToolCallCapabilityStore();
        var facade = Facade(capabilities);

        Assert.Equal(
            ManagementErrorType.NotFound,
            facade.SetModelToolDialect("nope", Model, new ModelToolDialectWriteRequest("constrained")).ErrorType);

        Assert.Equal(
            ManagementErrorType.NotFound,
            facade.SetModelToolDialect(Provider, "no-such-model", new ModelToolDialectWriteRequest("constrained")).ErrorType);
    }

    [Fact]
    public void WithNoCapabilityStore_TheOverrideReportsItselfUnavailable()
    {
        var result = Facade(capabilityStore: null)
            .SetModelToolDialect(Provider, Model, new ModelToolDialectWriteRequest("constrained"));

        Assert.Equal(ManagementErrorType.Unavailable, result.ErrorType);
    }

    [Fact]
    public void ThePinnedDialect_IsSurfacedThroughListProviders()
    {
        // BuildProvidersResponse is what the Governance dropdown renders from, so a pin that is stored but
        // invisible there would read to the operator as though nothing happened.
        var capabilities = _temp.CreateToolCallCapabilityStore();
        var facade = Facade(capabilities);

        facade.SetModelToolDialect(Provider, Model, new ModelToolDialectWriteRequest("constrained"));

        var model = Assert.Single(Assert.Single(facade.ListProviders().Providers).Models);
        Assert.Equal("constrained", model.Dialect);
        Assert.Equal(nameof(DetectionConfidence.Operator), model.Confidence, ignoreCase: true);
    }

    private ManagementFacade Facade(ToolCallCapabilityStore? capabilityStore)
    {
        var store = new InMemoryProviderConfigStore(new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                [Provider] = new ProviderOptions { BaseUrl = "http://localhost:1234/v1" },
            },
            ModelList = [new ModelRouteEntry { ModelName = Model, Provider = Provider, ProviderModelId = Model }],
        });

        return new ManagementFacade(
            store, Mock.Of<IEnvironmentVariableProvider>(), new HttpClient(), null, null, capabilityStore);
    }

    public void Dispose() => _temp.Dispose();
}

