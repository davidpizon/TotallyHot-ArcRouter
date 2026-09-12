using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Admin.Contract;

namespace TotallyHot.ArcRouter.Gui.Admin.Tests;

/// <summary>
/// Unit coverage for <see cref="ProviderAdminClient"/>: request-message mapping and response
/// (de)serialization against a stubbed generated client, plus admin-token metadata and error translation.
/// Driven through a subclassed generated stub rather than a live server, the same seam
/// <c>TotallyHot.ArcRouter.Gui.Telemetry.PriceSourceAdminClientTests</c> established for gRPC clients in
/// this codebase - the generated client exposes a protected parameterless constructor precisely for this,
/// and overriding the <c>CallOptions</c> overload catches the convenience overloads too (they delegate to
/// it).
/// </summary>
public sealed class ProviderAdminClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Contract.ProviderListResponse ListResponse(params Contract.ProviderState[] providers)
    {
        var response = new Contract.ProviderListResponse();
        response.Providers.AddRange(providers);
        return response;
    }

    private static Contract.ProviderState Provider(string key, string baseUrl = "https://api.openai.com",
        bool enabled = true, params Contract.ModelState[] models)
    {
        var provider = new Contract.ProviderState
        {
            Key = key, BaseUrl = baseUrl, AuthHeaderName = "Authorization", DollarSpent = "0", Enabled = enabled,
            WindowKind = "Monthly"
        };
        provider.Models.AddRange(models);
        return provider;
    }

    [Fact]
    public async Task GetProvidersAsync_MapsProvidersAndModels()
    {
        var stub = new StubClient
        {
            ListProvidersResponse = ListResponse(Provider(key: "openai",
                models: new Contract.ModelState { ModelName = "gpt-5.4", ProviderModelId = "gpt-5.4" }))
        };
        var client = new ProviderAdminClient(stub);

        var providers = await client.GetProvidersAsync(Ct);

        var provider = Assert.Single(providers);
        Assert.Equal(expected: "openai", actual: provider.Key);
        Assert.Equal(expected: "gpt-5.4", actual: Assert.Single(provider.Models).ModelName);
    }

    [Fact]
    public async Task GetProvidersAsync_MapsBudgetCapsAndSpend()
    {
        var provider = Provider("openai");
        provider.DollarCap = "500";
        provider.TokenCap = 1_000_000;
        provider.DollarSpent = "12.5";
        provider.TokensUsed = 500;
        var stub = new StubClient { ListProvidersResponse = ListResponse(provider) };
        var client = new ProviderAdminClient(stub);

        var result = Assert.Single(await client.GetProvidersAsync(Ct));

        Assert.Equal(500.0m, actual: result.DollarCap);
        Assert.Equal(1_000_000L, actual: result.TokenCap);
        Assert.Equal(12.5m, actual: result.DollarSpent);
        Assert.Equal(500L, actual: result.TokensUsed);
    }

    [Fact]
    public async Task GetProvidersAsync_MissingName_DeserializesAsNull()
    {
        var stub = new StubClient { ListProvidersResponse = ListResponse(Provider("openai")) };
        var client = new ProviderAdminClient(stub);

        Assert.Null(Assert.Single(await client.GetProvidersAsync(Ct)).Name);
    }

    [Fact]
    public async Task GetProvidersAsync_MapsDialectAndEndpointCapabilities()
    {
        var provider = Provider(key: "lmstudio", baseUrl: "http://localhost:1234/v1",
            models: new Contract.ModelState
            {
                ModelName = "qwen2.5-coder", ProviderModelId = "qwen2.5-coder", Dialect = "hermes",
                Confidence = "Observed", Enabled = false, PresentUpstream = false
            });
        provider.EndpointCapabilities = new Contract.EndpointCapabilitiesState
        {
            OpenaiCompatible = true, LmStudioNative = true, OllamaNative = false, AnthropicCompatible = false,
            ScannedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-31T00:00:00Z"))
        };
        var stub = new StubClient { ListProvidersResponse = ListResponse(provider) };
        var client = new ProviderAdminClient(stub);

        var result = Assert.Single(await client.GetProvidersAsync(Ct));

        var model = Assert.Single(result.Models);
        Assert.Equal(expected: "hermes", actual: model.Dialect);
        Assert.Equal(expected: "Observed", actual: model.Confidence);
        Assert.False(model.Enabled);
        Assert.False(model.PresentUpstream);
        Assert.True(result.EndpointCapabilities!.OpenAiCompatible);
        Assert.True(result.EndpointCapabilities.LmStudioNative);
    }

    [Fact]
    public async Task UpsertProviderAsync_SendsEveryConfiguredField()
    {
        var stub = new StubClient { UpsertProviderResponse = ListResponse(Provider("ollama")) };
        var client = new ProviderAdminClient(stub);

        await client.UpsertProviderAsync(
            key: "ollama",
            body: new ProviderWriteRequest(BaseUrl: "http://localhost:11434/v1", AuthHeaderName: "Authorization",
                IsFree: true, ProviderName: "Ollama"),
            cancellationToken: Ct);

        var request = stub.LastUpsertProviderRequest!;
        Assert.Equal(expected: "ollama", actual: request.Key);
        Assert.Equal(expected: "http://localhost:11434/v1", actual: request.BaseUrl);
        Assert.True(request.IsFree);
        Assert.Equal(expected: "Ollama", actual: request.ProviderName);
    }

    [Fact]
    public async Task UpsertProviderAsync_NullHeaders_DoesNotReplaceHeaders()
    {
        var stub = new StubClient { UpsertProviderResponse = ListResponse(Provider("openai")) };
        var client = new ProviderAdminClient(stub);

        await client.UpsertProviderAsync(key: "openai",
            body: new ProviderWriteRequest(BaseUrl: null, AuthHeaderName: null), cancellationToken: Ct);

        Assert.False(stub.LastUpsertProviderRequest!.ReplaceHeaders);
        Assert.Empty(stub.LastUpsertProviderRequest.Headers);
    }

    [Fact]
    public async Task UpsertProviderAsync_WithHeaders_SetsReplaceHeadersAndTheHeaderList()
    {
        var stub = new StubClient { UpsertProviderResponse = ListResponse(Provider("openai")) };
        var client = new ProviderAdminClient(stub);

        await client.UpsertProviderAsync(key: "openai",
            body: new ProviderWriteRequest(BaseUrl: null, AuthHeaderName: null,
                Headers: [new ProviderHeaderWriteModel(Name: "anthropic-version", Value: "2023-06-01", null)]),
            cancellationToken: Ct);

        Assert.True(stub.LastUpsertProviderRequest!.ReplaceHeaders);
        var header = Assert.Single(stub.LastUpsertProviderRequest.Headers);
        Assert.Equal(expected: "anthropic-version", actual: header.Name);
        Assert.Equal(expected: "2023-06-01", actual: header.Value);
    }

    [Fact]
    public async Task RemoveProviderAsync_SendsTheKey()
    {
        var stub = new StubClient { RemoveProviderResponse = new Contract.ProviderListResponse() };
        var client = new ProviderAdminClient(stub);

        await client.RemoveProviderAsync(key: "openai", cancellationToken: Ct);

        Assert.Equal(expected: "openai", actual: stub.LastRemoveProviderRequest!.Key);
    }

    [Fact]
    public async Task SetBudgetAsync_SendsCaps()
    {
        var stub = new StubClient { SetBudgetResponse = ListResponse(Provider("openai")) };
        var client = new ProviderAdminClient(stub);

        await client.SetBudgetAsync(key: "openai", body: new ProviderBudgetWriteRequest(250m, 2_000_000L), Ct);

        var budget = stub.LastSetBudgetRequest!.Budget;
        Assert.Equal(expected: "openai", actual: stub.LastSetBudgetRequest.ProviderKey);
        Assert.Equal(expected: "250", actual: budget.DollarCap);
        Assert.Equal(2_000_000L, actual: budget.TokenCap);
    }

    [Fact]
    public async Task SetBudgetAsync_NullCaps_LeavePresenceUnset()
    {
        var stub = new StubClient { SetBudgetResponse = ListResponse(Provider("openai")) };
        var client = new ProviderAdminClient(stub);

        await client.SetBudgetAsync(key: "openai", body: new ProviderBudgetWriteRequest(null, null), Ct);

        var budget = stub.LastSetBudgetRequest!.Budget;
        Assert.False(budget.HasDollarCap);
        Assert.False(budget.HasTokenCap);
    }

    [Fact]
    public async Task SetEnabledAsync_SendsTheKeyAndState()
    {
        var stub = new StubClient { SetEnabledResponse = ListResponse(Provider(key: "openai", enabled: false)) };
        var client = new ProviderAdminClient(stub);

        var result = await client.SetEnabledAsync(key: "openai", body: new ProviderEnabledWriteRequest(false), Ct);

        Assert.Equal(expected: "openai", actual: stub.LastSetEnabledRequest!.Key);
        Assert.False(stub.LastSetEnabledRequest.Enabled);
        Assert.False(Assert.Single(result).Enabled);
    }

    [Fact]
    public async Task UpsertModelAsync_SendsProviderKeyModelNameAndUpstreamId()
    {
        var stub = new StubClient { UpsertModelResponse = ListResponse(Provider("ollama")) };
        var client = new ProviderAdminClient(stub);

        await client.UpsertModelAsync(key: "ollama", modelName: "llama3", body: new ModelWriteRequest("llama3"), Ct);

        Assert.Equal(expected: "ollama", actual: stub.LastUpsertModelRequest!.ProviderKey);
        Assert.Equal(expected: "llama3", actual: stub.LastUpsertModelRequest.ModelName);
        Assert.Equal(expected: "llama3", actual: stub.LastUpsertModelRequest.Model.ProviderModelId);
    }

    [Fact]
    public async Task RemoveModelAsync_SendsTheModelName()
    {
        var stub = new StubClient { RemoveModelResponse = new Contract.ProviderListResponse() };
        var client = new ProviderAdminClient(stub);

        await client.RemoveModelAsync(key: "ollama", modelName: "llama3", Ct);

        Assert.Equal(expected: "llama3", actual: stub.LastRemoveModelRequest!.ModelName);
    }

    [Fact]
    public async Task SetModelEnabledAsync_SendsTheModelNameAndState()
    {
        var stub = new StubClient { SetModelEnabledResponse = new Contract.ProviderListResponse() };
        var client = new ProviderAdminClient(stub);

        await client.SetModelEnabledAsync(key: "openai", modelName: "gpt-5.4",
            body: new ModelEnabledWriteRequest(false), Ct);

        Assert.Equal(expected: "gpt-5.4", actual: stub.LastSetModelEnabledRequest!.ModelName);
        Assert.False(stub.LastSetModelEnabledRequest.Enabled);
    }

    [Fact]
    public async Task SetModelToolDialectAsync_SendsTheDialect()
    {
        var stub = new StubClient { SetModelToolDialectResponse = new Contract.ProviderListResponse() };
        var client = new ProviderAdminClient(stub);

        await client.SetModelToolDialectAsync(key: "openai", modelName: "gpt-5.4",
            body: new ModelToolDialectWriteRequest("constrained"), Ct);

        Assert.Equal(expected: "constrained", actual: stub.LastSetModelToolDialectRequest!.Dialect);
    }

    [Fact]
    public async Task SetModelToolDialectAsync_NullDialect_SendsAnEmptyString()
    {
        // Clearing the pin is the undo, so it must reach the server as an explicit clearing value rather
        // than being dropped from the request.
        var stub = new StubClient { SetModelToolDialectResponse = new Contract.ProviderListResponse() };
        var client = new ProviderAdminClient(stub);

        await client.SetModelToolDialectAsync(key: "openai", modelName: "gpt-5.4",
            body: new ModelToolDialectWriteRequest(null), Ct);

        Assert.Equal(expected: string.Empty, actual: stub.LastSetModelToolDialectRequest!.Dialect);
    }

    [Fact]
    public async Task DiscoverModelsAsync_MapsSupportedModelsAndError()
    {
        var stub = new StubClient
        {
            DiscoverModelsResponse = new Contract.DiscoverModelsResponse { Supported = true }
        };
        stub.DiscoverModelsResponse.Models.AddRange(["gpt-5.4", "gpt-4o"]);
        var client = new ProviderAdminClient(stub);

        var result = await client.DiscoverModelsAsync(key: "openai", Ct);

        Assert.True(result.Supported);
        Assert.Equal(expected: ["gpt-5.4", "gpt-4o"], actual: result.Models);
        Assert.Equal(expected: "openai", actual: stub.LastDiscoverModelsRequest!.ProviderKey);
    }

    [Fact]
    public async Task ScanCapabilitiesAsync_NarrowsTheRefreshedListToTheScannedProvider()
    {
        // The RPC returns the full refreshed list (every mutation on this service does); this method's own
        // contract is the single scanned provider's capabilities - unchanged from the REST-era client.
        var provider = Provider("lmstudio");
        provider.EndpointCapabilities = new Contract.EndpointCapabilitiesState
        {
            OpenaiCompatible = true, LmStudioNative = true,
            ScannedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-31T00:00:00Z"))
        };
        var stub = new StubClient
        {
            ScanCapabilitiesResponse = ListResponse(Provider("openai"), provider)
        };
        var client = new ProviderAdminClient(stub);

        var result = await client.ScanCapabilitiesAsync(key: "lmstudio", Ct);

        Assert.True(result.OpenAiCompatible);
        Assert.True(result.LmStudioNative);
        Assert.False(result.OllamaNative);
        Assert.Equal(expected: "lmstudio", actual: stub.LastScanCapabilitiesRequest!.ProviderKey);
    }

    [Fact]
    public async Task ScanCapabilitiesAsync_ProviderMissingCapabilities_Throws()
    {
        var stub = new StubClient { ScanCapabilitiesResponse = ListResponse(Provider("openai")) };
        var client = new ProviderAdminClient(stub);

        await Assert.ThrowsAsync<ProviderAdminException>(() =>
            client.ScanCapabilitiesAsync(key: "openai", Ct));
    }

    [Fact]
    public async Task RefreshFromEndpointAsync_SendsTheKey_AndReturnsTheRefreshedList()
    {
        var stub = new StubClient { RefreshFromEndpointResponse = ListResponse(Provider("openai")) };
        var client = new ProviderAdminClient(stub);

        var providers = await client.RefreshFromEndpointAsync(key: "openai", Ct);

        Assert.Single(providers);
        Assert.Equal(expected: "openai", actual: stub.LastRefreshFromEndpointRequest!.ProviderKey);
    }

    [Fact]
    public async Task GetPriceOverridesAsync_MapsEveryOverride()
    {
        var response = new Contract.PriceOverrideListResponse();
        response.Overrides.Add(new Contract.PriceOverride
        { SourceName = "LiteLLM", AggregatorModelKey = "gpt-5.4", ModelName = "gpt-5.4" });
        var stub = new StubClient { ListPriceOverridesResponse = response };
        var client = new ProviderAdminClient(stub);

        var overrides = await client.GetPriceOverridesAsync(Ct);

        var over = Assert.Single(overrides);
        Assert.Equal(expected: "LiteLLM", actual: over.SourceName);
    }

    [Fact]
    public async Task SetPriceOverrideAsync_SendsTheOverride()
    {
        var stub = new StubClient { SetPriceOverrideResponse = new Contract.PriceOverrideListResponse() };
        var client = new ProviderAdminClient(stub);

        await client.SetPriceOverrideAsync(new PriceOverrideWriteRequest("LiteLLM", "gpt-5.4", "gpt-5.4"), Ct);

        var over = stub.LastSetPriceOverrideRequest!.Override;
        Assert.Equal(expected: "LiteLLM", actual: over.SourceName);
        Assert.Equal(expected: "gpt-5.4", actual: over.AggregatorModelKey);
    }

    [Fact]
    public async Task RemovePriceOverrideAsync_SendsSourceAndKey()
    {
        var stub = new StubClient { RemovePriceOverrideResponse = new Contract.PriceOverrideListResponse() };
        var client = new ProviderAdminClient(stub);

        await client.RemovePriceOverrideAsync(sourceName: "LiteLLM", aggregatorModelKey: "gpt-5.4", Ct);

        Assert.Equal(expected: "LiteLLM", actual: stub.LastRemovePriceOverrideRequest!.SourceName);
        Assert.Equal(expected: "gpt-5.4", actual: stub.LastRemovePriceOverrideRequest.AggregatorModelKey);
    }

    [Fact]
    public async Task GetPriceResolutionDiagnosisAsync_MapsEveryEntry()
    {
        var response = new Contract.PriceResolutionResponse();
        response.Entries.Add(new Contract.PriceResolutionEntry
        { ModelName = "gpt-5.4", Provider = "openai", Resolved = true, IsApproximate = false });
        var stub = new StubClient { PriceResolutionResponse = response };
        var client = new ProviderAdminClient(stub);

        var entries = await client.GetPriceResolutionDiagnosisAsync(Ct);

        var entry = Assert.Single(entries);
        Assert.True(entry.Resolved);
        Assert.False(entry.IsApproximate);
    }

    [Fact]
    public async Task GetRateLimitHistoryAsync_SendsTheHoursAndMapsThePoints()
    {
        var response = new Contract.RateLimitHistoryResponse();
        var series = new Contract.RateLimitHistorySeries();
        series.Points.Add(new Contract.RateLimitHistoryPoint
        {
            BucketUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-01T12:00:00Z")), Remaining = 1000,
            Limit = 2000
        });
        response.Dimensions["tokens"] = series;
        var stub = new StubClient { RateLimitHistoryResponse = response };
        var client = new ProviderAdminClient(stub);

        var result = await client.GetRateLimitHistoryAsync(key: "openai", 3.5, Ct);

        Assert.Equal(expected: "openai", actual: stub.LastRateLimitHistoryRequest!.ProviderKey);
        Assert.Equal(3.5, actual: stub.LastRateLimitHistoryRequest.Hours);
        var points = result.Dimensions["tokens"];
        Assert.Equal(1000, actual: Assert.Single(points).Remaining);
    }

    [Fact]
    public async Task SetAdminApiKeyAsync_SendsTheReconciliationSecretName()
    {
        var stub = new StubClient { SetSecretResponse = new Contract.SetSecretResponse() };
        var client = new ProviderAdminClient(stub);

        await client.SetAdminApiKeyAsync(provider: "openai", value: "sk-abc", Ct);

        Assert.Equal(expected: "reconciliation:openai:admin-key", actual: stub.LastSetSecretRequest!.Name);
        Assert.Equal(expected: "sk-abc", actual: stub.LastSetSecretRequest.Value);
    }

    [Fact]
    public async Task DeleteAdminApiKeyAsync_SendsTheReconciliationSecretName()
    {
        var stub = new StubClient { DeleteSecretResponse = new Contract.DeleteSecretResponse() };
        var client = new ProviderAdminClient(stub);

        await client.DeleteAdminApiKeyAsync(provider: "anthropic", Ct);

        Assert.Equal(expected: "reconciliation:anthropic:admin-key", actual: stub.LastDeleteSecretRequest!.Name);
    }

    // --- admin token metadata ---

    [Fact]
    public async Task AdminToken_WhenConfigured_IsSentAsMetadata()
    {
        var stub = new StubClient { ListProvidersResponse = new Contract.ProviderListResponse() };
        var client = new ProviderAdminClient(stub, adminToken: "s3cret");

        await client.GetProvidersAsync(Ct);

        var entry = Assert.Single(stub.LastCallOptions!.Value.Headers!.GetAll("x-admin-token"));
        Assert.Equal(expected: "s3cret", actual: entry.Value);
    }

    [Fact]
    public async Task AdminToken_WhenNotConfigured_IsNotSent()
    {
        var stub = new StubClient { ListProvidersResponse = new Contract.ProviderListResponse() };
        var client = new ProviderAdminClient(stub);

        await client.GetProvidersAsync(Ct);

        Assert.Empty(stub.LastCallOptions!.Value.Headers!.GetAll("x-admin-token"));
    }

    // --- error handling ---

    [Fact]
    public async Task Unavailable_BecomesTheReachabilityMessage()
    {
        var stub = new StubClient
        { Failure = new RpcException(new Status(statusCode: StatusCode.Unavailable, detail: "failed to connect")) };
        var client = new ProviderAdminClient(stub);

        var ex = await Assert.ThrowsAsync<ProviderAdminException>(() => client.GetProvidersAsync(Ct));

        Assert.Contains(expectedSubstring: "Could not reach the proxy management API", actualString: ex.Message,
            comparisonType: StringComparison.Ordinal);
        Assert.IsType<RpcException>(ex.InnerException);
    }

    [Fact]
    public async Task ServerRejection_KeepsTheServersOwnDetail()
    {
        var stub = new StubClient
        {
            Failure = new RpcException(new Status(statusCode: StatusCode.InvalidArgument,
                detail: "ModelList entry 'x' references unknown provider 'y'."))
        };
        var client = new ProviderAdminClient(stub);

        var ex = await Assert.ThrowsAsync<ProviderAdminException>(() =>
            client.RemoveProviderAsync(key: "openai", Ct));

        Assert.Contains(expectedSubstring: "unknown provider", actualString: ex.Message,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_NullChannel_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ProviderAdminClient((Grpc.Net.Client.GrpcChannel)null!));
    }

    /// <summary>
    /// A generated-client test double. Overrides only the <c>CallOptions</c> overloads: the generated
    /// convenience overloads delegate to them, so this intercepts both call shapes.
    /// </summary>
    private sealed class StubClient : Contract.ProviderAdminService.ProviderAdminServiceClient
    {
        public Contract.ProviderListResponse ListProvidersResponse { get; init; } = new();
        public Contract.ProviderListResponse UpsertProviderResponse { get; init; } = new();
        public Contract.ProviderListResponse RemoveProviderResponse { get; init; } = new();
        public Contract.ProviderListResponse SetBudgetResponse { get; init; } = new();
        public Contract.ProviderListResponse SetEnabledResponse { get; init; } = new();
        public Contract.ProviderListResponse UpsertModelResponse { get; init; } = new();
        public Contract.ProviderListResponse RemoveModelResponse { get; init; } = new();
        public Contract.ProviderListResponse SetModelEnabledResponse { get; init; } = new();
        public Contract.ProviderListResponse SetModelToolDialectResponse { get; init; } = new();
        public Contract.DiscoverModelsResponse DiscoverModelsResponse { get; init; } = new();
        public Contract.ProviderListResponse ScanCapabilitiesResponse { get; init; } = new();
        public Contract.ProviderListResponse RefreshFromEndpointResponse { get; init; } = new();
        public Contract.PriceOverrideListResponse ListPriceOverridesResponse { get; init; } = new();
        public Contract.PriceOverrideListResponse SetPriceOverrideResponse { get; init; } = new();
        public Contract.PriceOverrideListResponse RemovePriceOverrideResponse { get; init; } = new();
        public Contract.PriceResolutionResponse PriceResolutionResponse { get; init; } = new();
        public Contract.RateLimitHistoryResponse RateLimitHistoryResponse { get; init; } = new();
        public Contract.SetSecretResponse SetSecretResponse { get; init; } = new();
        public Contract.DeleteSecretResponse DeleteSecretResponse { get; init; } = new();

        public RpcException? Failure { get; init; }

        public Contract.UpsertProviderRequest? LastUpsertProviderRequest { get; private set; }
        public Contract.RemoveProviderRequest? LastRemoveProviderRequest { get; private set; }
        public Contract.SetProviderBudgetRequest? LastSetBudgetRequest { get; private set; }
        public Contract.SetProviderEnabledRequest? LastSetEnabledRequest { get; private set; }
        public Contract.UpsertModelRequest? LastUpsertModelRequest { get; private set; }
        public Contract.RemoveModelRequest? LastRemoveModelRequest { get; private set; }
        public Contract.SetModelEnabledRequest? LastSetModelEnabledRequest { get; private set; }
        public Contract.SetModelToolDialectRequest? LastSetModelToolDialectRequest { get; private set; }
        public Contract.DiscoverModelsRequest? LastDiscoverModelsRequest { get; private set; }
        public Contract.ScanCapabilitiesRequest? LastScanCapabilitiesRequest { get; private set; }
        public Contract.RefreshFromEndpointRequest? LastRefreshFromEndpointRequest { get; private set; }
        public Contract.SetPriceOverrideRequest? LastSetPriceOverrideRequest { get; private set; }
        public Contract.RemovePriceOverrideRequest? LastRemovePriceOverrideRequest { get; private set; }
        public Contract.GetRateLimitHistoryRequest? LastRateLimitHistoryRequest { get; private set; }
        public Contract.SetSecretRequest? LastSetSecretRequest { get; private set; }
        public Contract.DeleteSecretRequest? LastDeleteSecretRequest { get; private set; }
        public CallOptions? LastCallOptions { get; private set; }

        public override AsyncUnaryCall<Contract.ProviderListResponse> ListProvidersAsync(
            Contract.ListProvidersRequest request, CallOptions options)
        {
            LastCallOptions = options;
            return Call(ListProvidersResponse);
        }

        public override AsyncUnaryCall<Contract.ProviderListResponse> UpsertProviderAsync(
            Contract.UpsertProviderRequest request, CallOptions options)
        {
            LastUpsertProviderRequest = request;
            LastCallOptions = options;
            return Call(UpsertProviderResponse);
        }

        public override AsyncUnaryCall<Contract.ProviderListResponse> RemoveProviderAsync(
            Contract.RemoveProviderRequest request, CallOptions options)
        {
            LastRemoveProviderRequest = request;
            LastCallOptions = options;
            return Call(RemoveProviderResponse);
        }

        public override AsyncUnaryCall<Contract.ProviderListResponse> SetProviderBudgetAsync(
            Contract.SetProviderBudgetRequest request, CallOptions options)
        {
            LastSetBudgetRequest = request;
            LastCallOptions = options;
            return Call(SetBudgetResponse);
        }

        public override AsyncUnaryCall<Contract.ProviderListResponse> SetProviderEnabledAsync(
            Contract.SetProviderEnabledRequest request, CallOptions options)
        {
            LastSetEnabledRequest = request;
            LastCallOptions = options;
            return Call(SetEnabledResponse);
        }

        public override AsyncUnaryCall<Contract.ProviderListResponse> UpsertModelAsync(
            Contract.UpsertModelRequest request, CallOptions options)
        {
            LastUpsertModelRequest = request;
            LastCallOptions = options;
            return Call(UpsertModelResponse);
        }

        public override AsyncUnaryCall<Contract.ProviderListResponse> RemoveModelAsync(
            Contract.RemoveModelRequest request, CallOptions options)
        {
            LastRemoveModelRequest = request;
            LastCallOptions = options;
            return Call(RemoveModelResponse);
        }

        public override AsyncUnaryCall<Contract.ProviderListResponse> SetModelEnabledAsync(
            Contract.SetModelEnabledRequest request, CallOptions options)
        {
            LastSetModelEnabledRequest = request;
            LastCallOptions = options;
            return Call(SetModelEnabledResponse);
        }

        public override AsyncUnaryCall<Contract.ProviderListResponse> SetModelToolDialectAsync(
            Contract.SetModelToolDialectRequest request, CallOptions options)
        {
            LastSetModelToolDialectRequest = request;
            LastCallOptions = options;
            return Call(SetModelToolDialectResponse);
        }

        public override AsyncUnaryCall<Contract.DiscoverModelsResponse> DiscoverModelsAsync(
            Contract.DiscoverModelsRequest request, CallOptions options)
        {
            LastDiscoverModelsRequest = request;
            LastCallOptions = options;
            return Call(DiscoverModelsResponse);
        }

        public override AsyncUnaryCall<Contract.ProviderListResponse> ScanCapabilitiesAsync(
            Contract.ScanCapabilitiesRequest request, CallOptions options)
        {
            LastScanCapabilitiesRequest = request;
            LastCallOptions = options;
            return Call(ScanCapabilitiesResponse);
        }

        public override AsyncUnaryCall<Contract.ProviderListResponse> RefreshFromEndpointAsync(
            Contract.RefreshFromEndpointRequest request, CallOptions options)
        {
            LastRefreshFromEndpointRequest = request;
            LastCallOptions = options;
            return Call(RefreshFromEndpointResponse);
        }

        public override AsyncUnaryCall<Contract.PriceOverrideListResponse> ListPriceOverridesAsync(
            Contract.ListPriceOverridesRequest request, CallOptions options)
        {
            LastCallOptions = options;
            return Call(ListPriceOverridesResponse);
        }

        public override AsyncUnaryCall<Contract.PriceOverrideListResponse> SetPriceOverrideAsync(
            Contract.SetPriceOverrideRequest request, CallOptions options)
        {
            LastSetPriceOverrideRequest = request;
            LastCallOptions = options;
            return Call(SetPriceOverrideResponse);
        }

        public override AsyncUnaryCall<Contract.PriceOverrideListResponse> RemovePriceOverrideAsync(
            Contract.RemovePriceOverrideRequest request, CallOptions options)
        {
            LastRemovePriceOverrideRequest = request;
            LastCallOptions = options;
            return Call(RemovePriceOverrideResponse);
        }

        public override AsyncUnaryCall<Contract.PriceResolutionResponse> GetPriceResolutionAsync(
            Contract.GetPriceResolutionRequest request, CallOptions options)
        {
            LastCallOptions = options;
            return Call(PriceResolutionResponse);
        }

        public override AsyncUnaryCall<Contract.RateLimitHistoryResponse> GetRateLimitHistoryAsync(
            Contract.GetRateLimitHistoryRequest request, CallOptions options)
        {
            LastRateLimitHistoryRequest = request;
            LastCallOptions = options;
            return Call(RateLimitHistoryResponse);
        }

        public override AsyncUnaryCall<Contract.SetSecretResponse> SetSecretAsync(
            Contract.SetSecretRequest request, CallOptions options)
        {
            LastSetSecretRequest = request;
            LastCallOptions = options;
            return Call(SetSecretResponse);
        }

        public override AsyncUnaryCall<Contract.DeleteSecretResponse> DeleteSecretAsync(
            Contract.DeleteSecretRequest request, CallOptions options)
        {
            LastDeleteSecretRequest = request;
            LastCallOptions = options;
            return Call(DeleteSecretResponse);
        }

        private AsyncUnaryCall<T> Call<T>(T response)
        {
            return new AsyncUnaryCall<T>(
                responseAsync: Failure is null ? Task.FromResult(response) : Task.FromException<T>(Failure),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }
    }
}
