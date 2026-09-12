using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using Contract = TotallyHot.ArcRouter.Admin.Contract;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// gRPC service backing the Governance UI's Providers card: provider/model CRUD, budgets, discovery,
/// endpoint capability scanning, price overrides, rate-limit history, and secrets
/// (docs/router/tracked-todos.md #7). Replaces <see cref="ProviderAdminEndpoints"/>'s plain-HTTP
/// <c>/admin/*</c> surface, which shared the LLM-forwarding proxy port with real traffic; this service is
/// mapped onto the same loopback TLS endpoint as <c>TelemetryService</c> instead. All logic - projection,
/// merging, credential/header masking, and validation - still lives in <see cref="ManagementFacade"/>, the
/// same facade the REST surface and the MCP endpoint's provider tools use, so every surface shares one
/// behavior. This class only translates gRPC requests into facade calls and
/// <see cref="ManagementResult{T}"/> outcomes into gRPC responses/status codes.
/// </summary>
public sealed class ProviderAdminGrpcService : Contract.ProviderAdminService.ProviderAdminServiceBase
{
    private readonly ManagementFacade _facade;

    /// <summary>Initializes a new instance of the <see cref="ProviderAdminGrpcService"/> class.</summary>
    /// <param name="facade">The shared management facade backing every read/write.</param>
    public ProviderAdminGrpcService(ManagementFacade facade)
    {
        ArgumentNullException.ThrowIfNull(facade);
        _facade = facade;
    }

    /// <inheritdoc/>
    public override Task<Contract.ProviderListResponse> ListProviders(Contract.ListProvidersRequest request,
        ServerCallContext context)
    {
        return Task.FromResult(ToWire(_facade.ListProviders()));
    }

    /// <inheritdoc/>
    public override async Task<Contract.ProviderListResponse> UpsertProvider(Contract.UpsertProviderRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var headers = request.ReplaceHeaders
            ? request.Headers.Select(h => new HeaderWriteRequest(
                Name: h.Name,
                Value: h.HasValue ? h.Value : null,
                ValueEnvVar: h.HasValueEnvVar ? h.ValueEnvVar : null,
                Locked: h.HasLocked ? h.Locked : null)).ToList()
            : null;

        var write = new ProviderWriteRequest(
            BaseUrl: request.HasBaseUrl ? request.BaseUrl : null,
            AuthHeaderName: request.HasAuthHeaderName ? request.AuthHeaderName : null,
            Headers: headers,
            IsFree: request.HasIsFree ? request.IsFree : null,
            Enabled: request.HasEnabled ? request.Enabled : null,
            ProviderName: request.HasProviderName ? request.ProviderName : null,
            ProviderType: request.HasProviderType ? request.ProviderType : null);

        var result = await _facade.UpsertProviderAsync(key: request.Key, request: write,
            cancellationToken: context.CancellationToken).ConfigureAwait(false);
        return ToWire(Unwrap(result));
    }

    /// <inheritdoc/>
    public override async Task<Contract.ProviderListResponse> RemoveProvider(Contract.RemoveProviderRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await _facade.RemoveProviderAsync(key: request.Key, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        return ToWire(Unwrap(result));
    }

    /// <inheritdoc/>
    public override Task<Contract.ProviderListResponse> SetProviderBudget(Contract.SetProviderBudgetRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var budget = request.Budget ?? throw new RpcException(new Status(statusCode: StatusCode.InvalidArgument,
            detail: "A budget is required."));
        var write = new ProviderBudgetWriteRequest(
            DollarCap: budget.HasDollarCap ? ParseDecimal(budget.DollarCap) : null,
            TokenCap: budget.HasTokenCap ? budget.TokenCap : null,
            WindowKind: budget.HasWindowKind ? budget.WindowKind : null,
            WindowHours: budget.HasWindowHours ? budget.WindowHours : null);

        var result = _facade.SetBudget(providerKey: request.ProviderKey, request: write);
        return Task.FromResult(ToWire(Unwrap(result)));
    }

    /// <inheritdoc/>
    public override async Task<Contract.ProviderListResponse> SetProviderEnabled(
        Contract.SetProviderEnabledRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await _facade.SetEnabledAsync(key: request.Key,
            request: new ProviderEnabledWriteRequest(request.Enabled),
            cancellationToken: context.CancellationToken).ConfigureAwait(false);
        return ToWire(Unwrap(result));
    }

    /// <inheritdoc/>
    public override async Task<Contract.ProviderListResponse> UpsertModel(Contract.UpsertModelRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = request.Model ?? throw new RpcException(new Status(statusCode: StatusCode.InvalidArgument,
            detail: "A model is required."));
        var write = new ModelWriteRequest(
            ProviderModelId: model.HasProviderModelId ? model.ProviderModelId : null);

        var result = await _facade.UpsertModelAsync(providerKey: request.ProviderKey, modelName: request.ModelName,
            request: write, cancellationToken: context.CancellationToken).ConfigureAwait(false);
        return ToWire(Unwrap(result));
    }

    /// <inheritdoc/>
    public override async Task<Contract.ProviderListResponse> RemoveModel(Contract.RemoveModelRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await _facade.RemoveModelAsync(modelName: request.ModelName,
            cancellationToken: context.CancellationToken).ConfigureAwait(false);
        return ToWire(Unwrap(result));
    }

    /// <inheritdoc/>
    public override async Task<Contract.ProviderListResponse> SetModelEnabled(Contract.SetModelEnabledRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await _facade.SetModelEnabledAsync(modelName: request.ModelName,
            request: new ModelEnabledWriteRequest(request.Enabled),
            cancellationToken: context.CancellationToken).ConfigureAwait(false);
        return ToWire(Unwrap(result));
    }

    /// <inheritdoc/>
    public override Task<Contract.ProviderListResponse> SetModelToolDialect(
        Contract.SetModelToolDialectRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = _facade.SetModelToolDialect(key: request.ProviderKey, modelName: request.ModelName,
            request: new ModelToolDialectWriteRequest(request.Dialect));
        return Task.FromResult(ToWire(Unwrap(result)));
    }

    /// <inheritdoc/>
    public override async Task<Contract.DiscoverModelsResponse> DiscoverModels(Contract.DiscoverModelsRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await _facade.DiscoverModelsAsync(providerKey: request.ProviderKey,
            cancellationToken: context.CancellationToken).ConfigureAwait(false);
        var value = Unwrap(result);
        var response = new Contract.DiscoverModelsResponse { Supported = value.Supported };
        response.Models.AddRange(value.Models);
        if (value.Error is not null) response.Error = value.Error;
        return response;
    }

    /// <inheritdoc/>
    public override async Task<Contract.ProviderListResponse> ScanCapabilities(Contract.ScanCapabilitiesRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await _facade.ScanCapabilitiesAsync(key: request.ProviderKey,
            cancellationToken: context.CancellationToken).ConfigureAwait(false);
        Unwrap(result);
        return ToWire(_facade.ListProviders());
    }

    /// <inheritdoc/>
    public override async Task<Contract.ProviderListResponse> RefreshFromEndpoint(
        Contract.RefreshFromEndpointRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await _facade.RefreshFromEndpointAsync(key: request.ProviderKey,
            cancellationToken: context.CancellationToken).ConfigureAwait(false);
        return ToWire(Unwrap(result));
    }

    /// <inheritdoc/>
    public override Task<Contract.PriceOverrideListResponse> ListPriceOverrides(
        Contract.ListPriceOverridesRequest request, ServerCallContext context)
    {
        var result = _facade.ListPriceOverrides();
        return Task.FromResult(ToWire(Unwrap(result)));
    }

    /// <inheritdoc/>
    public override Task<Contract.PriceOverrideListResponse> SetPriceOverride(Contract.SetPriceOverrideRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var overrideValue = request.Override ?? throw new RpcException(new Status(
            statusCode: StatusCode.InvalidArgument, detail: "An override is required."));
        var write = new PriceOverrideWriteRequest(
            SourceName: overrideValue.SourceName,
            AggregatorModelKey: overrideValue.AggregatorModelKey,
            ModelName: overrideValue.ModelName);
        var result = _facade.SetPriceOverride(write);
        return Task.FromResult(ToWire(Unwrap(result)));
    }

    /// <inheritdoc/>
    public override Task<Contract.PriceOverrideListResponse> RemovePriceOverride(
        Contract.RemovePriceOverrideRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = _facade.RemovePriceOverride(sourceName: request.SourceName,
            aggregatorModelKey: request.AggregatorModelKey);
        return Task.FromResult(ToWire(Unwrap(result)));
    }

    /// <inheritdoc/>
    public override Task<Contract.PriceResolutionResponse> GetPriceResolution(Contract.GetPriceResolutionRequest request,
        ServerCallContext context)
    {
        var result = _facade.GetPriceResolutionDiagnosis();
        var value = Unwrap(result);
        var response = new Contract.PriceResolutionResponse();
        response.Entries.AddRange(value.Select(e => new Contract.PriceResolutionEntry
        {
            ModelName = e.ModelName,
            Provider = e.Provider,
            Resolved = e.Resolved,
            IsApproximate = e.IsApproximate
        }));
        return Task.FromResult(response);
    }

    /// <inheritdoc/>
    public override Task<Contract.RateLimitHistoryResponse> GetRateLimitHistory(
        Contract.GetRateLimitHistoryRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hours = request.HasHours ? request.Hours : 6.0;
        var result = _facade.GetRateLimitHistory(providerKey: request.ProviderKey, hours: hours);
        var value = Unwrap(result);

        var response = new Contract.RateLimitHistoryResponse();
        foreach (var (dimension, points) in value.Dimensions)
        {
            var series = new Contract.RateLimitHistorySeries();
            series.Points.AddRange(points.Select(p =>
            {
                var wire = new Contract.RateLimitHistoryPoint { BucketUtc = Timestamp.FromDateTimeOffset(p.BucketUtc) };
                if (p.Remaining.HasValue) wire.Remaining = p.Remaining.Value;
                if (p.Limit.HasValue) wire.Limit = p.Limit.Value;
                return wire;
            }));
            response.Dimensions[dimension] = series;
        }

        return Task.FromResult(response);
    }

    /// <inheritdoc/>
    public override Task<Contract.SetSecretResponse> SetSecret(Contract.SetSecretRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = _facade.SetSecret(name: request.Name, value: request.Value);
        Unwrap(result);
        return Task.FromResult(new Contract.SetSecretResponse());
    }

    /// <inheritdoc/>
    public override Task<Contract.DeleteSecretResponse> DeleteSecret(Contract.DeleteSecretRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = _facade.DeleteSecret(request.Name);
        Unwrap(result);
        return Task.FromResult(new Contract.DeleteSecretResponse());
    }

    /// <summary>Returns a successful <see cref="ManagementResult{T}"/>'s value, or throws the equivalent <see cref="RpcException"/>.</summary>
    private static T Unwrap<T>(ManagementResult<T> result)
    {
        if (result.Success) return result.Value!;

        var statusCode = result.ErrorType switch
        {
            ManagementErrorType.NotFound => StatusCode.NotFound,
            ManagementErrorType.InvalidRequest => StatusCode.InvalidArgument,
            ManagementErrorType.Unavailable => StatusCode.Unavailable,
            _ => StatusCode.Internal
        };
        throw new RpcException(new Status(statusCode: statusCode, detail: result.ErrorMessage!));
    }

    /// <summary>Parses a decimal-as-string wire value, translating a malformed value into an INVALID_ARGUMENT status.</summary>
    private static decimal ParseDecimal(string value)
    {
        if (decimal.TryParse(s: value, style: NumberStyles.Number, provider: CultureInfo.InvariantCulture,
                result: out var parsed))
            return parsed;

        throw new RpcException(new Status(statusCode: StatusCode.InvalidArgument,
            detail: $"'{value}' is not a valid decimal amount."));
    }

    /// <summary>Projects a <see cref="ProvidersResponse"/> into its wire shape.</summary>
    private static Contract.ProviderListResponse ToWire(ProvidersResponse response)
    {
        var wire = new Contract.ProviderListResponse();
        wire.Providers.AddRange(response.Providers.Select(ToWire));
        return wire;
    }

    /// <summary>Projects a single <see cref="ProviderView"/> into its wire shape.</summary>
    private static Contract.ProviderState ToWire(ProviderView provider)
    {
        var wire = new Contract.ProviderState
        {
            Key = provider.Key,
            BaseUrl = provider.BaseUrl,
            AuthHeaderName = provider.AuthHeaderName,
            IsFree = provider.IsFree,
            DollarSpent = provider.DollarSpent.ToString(CultureInfo.InvariantCulture),
            TokensUsed = provider.TokensUsed,
            Enabled = provider.Enabled,
            WindowKind = provider.WindowKind,
            HasStoredAdminKey = provider.HasStoredAdminKey
        };

        if (provider.Name is not null) wire.Name = provider.Name;
        if (provider.DollarCap.HasValue) wire.DollarCap = provider.DollarCap.Value.ToString(CultureInfo.InvariantCulture);
        if (provider.TokenCap.HasValue) wire.TokenCap = provider.TokenCap.Value;
        if (provider.ProviderType is not null) wire.ProviderType = provider.ProviderType;
        if (provider.UsageLastRecordedAtUtc.HasValue)
            wire.UsageLastRecordedAtUtc = Timestamp.FromDateTimeOffset(provider.UsageLastRecordedAtUtc.Value);
        if (provider.NextResetUtc.HasValue) wire.NextResetUtc = Timestamp.FromDateTimeOffset(provider.NextResetUtc.Value);
        if (provider.EndpointCapabilities is { } capabilities) wire.EndpointCapabilities = ToWire(capabilities);
        if (provider.RateLimit is { } rateLimit) wire.RateLimit = ToWire(rateLimit);
        if (provider.ReportedUsage is { } reportedUsage) wire.ReportedUsage = ToWire(reportedUsage);
        if (provider.AdminAction is { } adminAction) wire.AdminAction = ToWire(adminAction);
        if (provider.LiveTraffic is { } liveTraffic) wire.LiveTraffic = ToWire(liveTraffic);

        wire.Models.AddRange(provider.Models.Select(ToWire));
        wire.Headers.AddRange(provider.Headers.Select(ToWire));
        return wire;
    }

    /// <summary>Projects a single <see cref="ModelView"/> into its wire shape.</summary>
    private static Contract.ModelState ToWire(ModelView model)
    {
        var wire = new Contract.ModelState
        {
            ModelName = model.ModelName,
            ProviderModelId = model.ProviderModelId,
            Enabled = model.Enabled,
            PresentUpstream = model.PresentUpstream
        };
        if (model.Dialect is not null) wire.Dialect = model.Dialect;
        if (model.Confidence is not null) wire.Confidence = model.Confidence;
        return wire;
    }

    /// <summary>Projects a single <see cref="HeaderView"/> into its masked wire shape.</summary>
    private static Contract.HeaderState ToWire(HeaderView header)
    {
        var wire = new Contract.HeaderState { Name = header.Name, Source = header.Source, Locked = header.Locked };
        if (header.ValueEnvVar is not null) wire.ValueEnvVar = header.ValueEnvVar;
        if (header.Value is not null) wire.Value = header.Value;
        return wire;
    }

    /// <summary>Projects a single <see cref="ProviderEndpointCapabilities"/> into its wire shape.</summary>
    private static Contract.EndpointCapabilitiesState ToWire(ProviderEndpointCapabilities capabilities)
    {
        var wire = new Contract.EndpointCapabilitiesState
        {
            OpenaiCompatible = capabilities.OpenAiCompatible,
            LmStudioNative = capabilities.LmStudioNative,
            OllamaNative = capabilities.OllamaNative,
            AnthropicCompatible = capabilities.AnthropicCompatible,
            JsonSchemaResponseFormat = capabilities.JsonSchemaResponseFormat,
            ScannedAtUtc = Timestamp.FromDateTimeOffset(capabilities.ScannedAtUtc)
        };
        if (capabilities.ScanError is not null) wire.ScanError = capabilities.ScanError;
        return wire;
    }

    /// <summary>Projects a single <see cref="ProviderReportedUsageView"/> into its wire shape.</summary>
    private static Contract.ProviderReportedUsageState ToWire(ProviderReportedUsageView reportedUsage)
    {
        var wire = new Contract.ProviderReportedUsageState
        {
            FetchedAtUtc = Timestamp.FromDateTimeOffset(reportedUsage.FetchedAtUtc)
        };
        wire.Rows.AddRange(reportedUsage.Rows.Select(row => new Contract.ReportedUsageRow
        {
            UsageDay = row.UsageDay.ToString(format: "yyyy-MM-dd", provider: CultureInfo.InvariantCulture),
            Model = row.Model,
            InputTokens = row.InputTokens,
            OutputTokens = row.OutputTokens,
            CacheCreationTokens = row.CacheCreationTokens,
            CacheReadTokens = row.CacheReadTokens
        }));
        return wire;
    }

    /// <summary>Projects a single <see cref="ProviderInteractionStatus"/> into its wire shape.</summary>
    private static Contract.ProviderInteractionState ToWire(ProviderInteractionStatus status)
    {
        var wire = new Contract.ProviderInteractionState
        {
            Ok = status.Ok,
            Operation = status.Operation,
            AtUtc = Timestamp.FromDateTimeOffset(status.AtUtc),
            Kind = status.Kind.ToString()
        };
        if (status.Message is not null) wire.Message = status.Message;
        return wire;
    }

    /// <summary>Projects a single <see cref="ProviderRateLimitView"/> into its wire shape.</summary>
    private static Contract.ProviderRateLimitState ToWire(ProviderRateLimitView rateLimit)
    {
        var wire = new Contract.ProviderRateLimitState
        {
            ObservedAtUtc = Timestamp.FromDateTimeOffset(rateLimit.ObservedAtUtc),
            IsStale = rateLimit.IsStale
        };

        foreach (var (dimensionName, dimension) in rateLimit.Snapshot.StandardDimensions)
        {
            var dimensionWire = new Contract.RateLimitDimensionState();
            if (dimension.Limit.HasValue) dimensionWire.Limit = dimension.Limit.Value;
            if (dimension.Remaining.HasValue) dimensionWire.Remaining = dimension.Remaining.Value;
            if (dimension.ResetAt.HasValue) dimensionWire.ResetAt = Timestamp.FromDateTimeOffset(dimension.ResetAt.Value);

            if (rateLimit.Projections.TryGetValue(key: dimensionName, value: out var projection))
            {
                dimensionWire.TimeToExhaustionSeconds = (long)projection.TimeToExhaustion.TotalSeconds;
                dimensionWire.BurnRatePerMinute = projection.BurnRatePerMinute;
            }

            wire.Dimensions[dimensionName] = dimensionWire;
        }

        foreach (var (windowName, window) in rateLimit.Snapshot.UnifiedWindows)
        {
            var windowWire = new Contract.UnifiedWindowState();
            if (window.Status is not null) windowWire.Status = window.Status;
            if (window.Remaining.HasValue) windowWire.Remaining = window.Remaining.Value;
            if (window.ResetAt.HasValue) windowWire.ResetAt = Timestamp.FromDateTimeOffset(window.ResetAt.Value);
            wire.UnifiedWindows[windowName] = windowWire;
        }

        return wire;
    }

    /// <summary>Projects the configured price overrides into their wire shape.</summary>
    private static Contract.PriceOverrideListResponse ToWire(IReadOnlyList<ModelAliasOverride> overrides)
    {
        var wire = new Contract.PriceOverrideListResponse();
        wire.Overrides.AddRange(overrides.Select(o => new Contract.PriceOverride
        {
            SourceName = o.SourceName,
            AggregatorModelKey = o.AggregatorModelKey,
            ModelName = o.ModelName
        }));
        return wire;
    }
}
