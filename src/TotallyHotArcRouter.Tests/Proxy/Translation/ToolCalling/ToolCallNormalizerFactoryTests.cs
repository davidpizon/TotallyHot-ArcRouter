using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Translation;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

/// <summary>
/// Covers the arming decision itself (<c>docs/router/tool-call-normalization.md</c> §3.4): a per-(provider,
/// model) capability lookup rather than a provider-wide flag.
/// <para>
/// Returning <see langword="null"/> is the outcome most of these assert, and it is the valuable one: it
/// restores true byte-for-byte forwarding. Two of the four performance rules live here rather than in the
/// scanner, because the cheapest scan is the one never installed.
/// </para>
/// </summary>
public class ToolCallNormalizerFactoryTests
{
    private const string Provider = "lmstudio";
    private const string Model = "qwen2.5.1-coder-7b-instruct";

    [Fact]
    public void ARequestOfferingNoTools_IsNotArmed()
    {
        // Rule 1. The shipping guard scanned every response on a guarded route regardless of whether tools
        // were ever offered, which is the great majority of them.
        Assert.Null(new ToolCallNormalizerFactory().TryCreate(route: Route(), false));
    }

    [Fact]
    public void AModelKnownToEmitNativeToolCalls_IsNotArmed()
    {
        // Rule 2, and the headline case provider-wide arming got wrong: a capable model on the same local
        // server as a misbehaving one must keep byte-for-byte forwarding.
        var store = new FakeToolCallCapabilityStore().Seed(Capability(dialect: "openai-native",
            confidence: DetectionConfidence.Observed));

        Assert.Null(new ToolCallNormalizerFactory(store).TryCreate(route: Route(), true));
    }

    [Fact]
    public void AnUnclassifiedModel_ArmsEveryScannableDialect()
    {
        // Tier 4: the first live tools-carrying request is its own probe. It is forwarded natively and
        // unmodified, so it still works, and the union scan reads the answer off whatever comes back.
        var plan = PlanFor(new ToolCallNormalizerFactory().TryCreate(route: Route(), true));

        Assert.Equal(expected: ToolCallDialectRegistry.ScannableDialects, actual: plan.Candidates);
        Assert.True(plan.IsObserving);
    }

    [Fact]
    public void AnObservedClassification_ArmsOnlyWhatTheModelSpeaks_AndStopsObserving()
    {
        var store = new FakeToolCallCapabilityStore().Seed(Capability(dialect: "mistral",
            confidence: DetectionConfidence.Observed));

        var plan = PlanFor(new ToolCallNormalizerFactory(store).TryCreate(route: Route(), true));

        Assert.Equal(expected: [ToolCallDialectRegistry.Mistral], actual: plan.Candidates);
        Assert.False(plan.IsObserving);
    }

    [Fact]
    public void AnOperatorPin_ArmsOnlyThePinnedDialect()
    {
        // The pin is the escape hatch for a model whose detection misfires; it must decide what is scanned,
        // not merely survive in the database.
        var store = new FakeToolCallCapabilityStore().Seed(Capability(dialect: "mistral",
            confidence: DetectionConfidence.Operator));

        var plan = PlanFor(new ToolCallNormalizerFactory(store).TryCreate(route: Route(), true));

        Assert.Equal(expected: [ToolCallDialectRegistry.Mistral], actual: plan.Candidates);
        Assert.False(plan.IsObserving);
    }

    [Theory]
    [InlineData(DetectionConfidence.Heuristic)]
    [InlineData(DetectionConfidence.Template)]
    public void AnUnconfirmedGuess_ArmsTheUnionWithTheGuessFirst_SoAWrongGuessCanBeCorrected(
        DetectionConfidence confidence)
    {
        // §3.2 says tier 4 "confirms or corrects" the lower tiers. Arming only the guessed dialect would
        // make a wrong guess permanent, because the evidence that would correct it is exactly what a
        // single-dialect scan discards.
        var store = new FakeToolCallCapabilityStore().Seed(Capability(dialect: "llama3-json", confidence: confidence));

        var plan = PlanFor(new ToolCallNormalizerFactory(store).TryCreate(route: Route(), true));

        Assert.Equal(expected: ToolCallDialectRegistry.Llama3Json, actual: plan.Candidates[0]);
        Assert.Equal(expected: ToolCallDialectRegistry.ScannableDialects.Count, actual: plan.Candidates.Count);
        Assert.True(plan.IsObserving);
    }

    [Fact]
    public void ADialectThisBuildDoesNotKnow_IsNotArmed()
    {
        // A row naming an unknown dialect was written by a newer build (or by hand). Guessing would mean
        // scanning with delimiters that build already decided were wrong - the documented contract on
        // ModelToolCapability.Dialect is to degrade to no scanning.
        var store = new FakeToolCallCapabilityStore().Seed(Capability(dialect: "deepseek-v4",
            confidence: DetectionConfidence.Observed));

        Assert.Null(new ToolCallNormalizerFactory(store).TryCreate(route: Route(), true));
    }

    [Fact]
    public void WithNoCapabilityStore_AToolsCarryingRequestIsStillNormalized()
    {
        // Direct construction outside DI. Normalization still works; nothing is classified or persisted.
        var plan = PlanFor(new ToolCallNormalizerFactory(capabilityStore: null).TryCreate(route: Route(), true));

        Assert.Equal(expected: ToolCallDialectRegistry.ScannableDialects, actual: plan.Candidates);
    }

    [Fact]
    public void TheArmedTranslator_IsResponseOnly_SoTheClientsRequestPathIsPreserved()
    {
        // Routing it through the request-reshaping path would drop the client's own request path, which an
        // OpenAI-shaped passthrough provider still needs - see IResponseOnlyTranslator.
        var translator = new ToolCallNormalizerFactory().TryCreate(route: Route(), true);

        Assert.IsAssignableFrom<IResponseOnlyTranslator>(translator);
    }

    private static ToolCallNormalizationPlan PlanFor(IPayloadTranslator? translator)
    {
        return Assert.IsType<ToolCallNormalizingTranslator>(translator).Plan;
    }

    private static ModelToolCapability Capability(string dialect, DetectionConfidence confidence)
    {
        return new ModelToolCapability(ProviderKey: Provider, ModelName: Model, Dialect: dialect,
            Confidence: confidence);
    }

    private static ResolvedModelRoute Route()
    {
        return new ResolvedModelRoute(
            ModelName: Model,
            Provider: Provider,
            ProviderModelId: Model,
            UpstreamBaseUrl: new Uri("http://127.0.0.1:1234/v1"),
            AuthHeaderName: "Authorization",
            ExtraHeaders: []);
    }
}