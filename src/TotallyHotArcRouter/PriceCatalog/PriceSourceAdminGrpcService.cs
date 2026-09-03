using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// gRPC service backing the Governance → Price Sources panel: reports each price feed's status, switches one
/// on or off (D6), and runs an ingestion cycle on demand. Mapped by <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/>
/// onto the same loopback TLS endpoint as <c>TelemetryService</c>.
/// </summary>
/// <remarks>
/// Carries feed metadata only - counts and timestamps, never a price. That is D5's licensing line, and this
/// channel being loopback-only is what permits the panel at all; it is not a licence to widen the payload.
/// See the comment on <c>PriceSourceAdminService</c> in <c>src/Protos/telemetry.proto</c>.
/// </remarks>
public sealed class PriceSourceAdminGrpcService : Contract.PriceSourceAdminService.PriceSourceAdminServiceBase
{
    private readonly PriceCatalogIngestionService _ingestionService;
    private readonly TimeSpan _pollInterval;
    private readonly PriceSourceToggleStore _toggleStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="PriceSourceAdminGrpcService"/> class.
    /// </summary>
    public PriceSourceAdminGrpcService(
        PriceSourceToggleStore toggleStore,
        PriceCatalogIngestionService ingestionService,
        IOptions<PriceCatalogOptions> options)
    {
        ArgumentNullException.ThrowIfNull(toggleStore);
        ArgumentNullException.ThrowIfNull(ingestionService);
        ArgumentNullException.ThrowIfNull(options);

        _toggleStore = toggleStore;
        _ingestionService = ingestionService;
        _pollInterval = TimeSpan.FromHours(options.Value.PollIntervalHours);
    }

    /// <inheritdoc/>
    public override Task<Contract.ListPriceSourcesResponse> ListPriceSources(
        Contract.ListPriceSourcesRequest request,
        ServerCallContext context)
    {
        return Task.FromResult(BuildListResponse());
    }

    /// <inheritdoc/>
    public override Task<Contract.ListPriceSourcesResponse> SetPriceSourceEnabled(
        Contract.SetPriceSourceEnabledRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Name))
            throw new RpcException(new Status(statusCode: StatusCode.InvalidArgument,
                detail: "A source name is required."));

        // NotFound rather than a silent no-op: a toggle that reports success while changing nothing is the
        // failure this whole surface exists to avoid.
        if (!_toggleStore.SetEnabled(sourceName: request.Name, enabled: request.Enabled))
            throw new RpcException(new Status(
                statusCode: StatusCode.NotFound,
                detail: $"No price source named '{request.Name}' exists."));

        return Task.FromResult(BuildListResponse());
    }

    /// <inheritdoc/>
    public override async Task<Contract.RefreshPriceSourcesResponse> RefreshPriceSources(
        Contract.RefreshPriceSourcesRequest request,
        ServerCallContext context)
    {
        return await RunCycleAndBuildResponseAsync(context.CancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task<Contract.RefreshPriceSourcesResponse> ReorderPriceSources(
        Contract.ReorderPriceSourcesRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Rejected outright rather than best-effort, per ReorderSources' contract: a partial reorder would
        // leave an unlisted source's rank stale relative to ranks that just moved.
        if (!_toggleStore.Reorder(request.SourceNamesInPriorityOrder))
            throw new RpcException(new Status(
                statusCode: StatusCode.InvalidArgument,
                detail: "The submitted order must name every existing price source exactly once."));

        // Re-resolve contested cells under the new order immediately, from prices already in storage - no
        // live pull. This is the panel's only reorder side effect now: "Pull Now" is the sole action that
        // reaches out to a source over the network.
        var summary = await _ingestionService.RecomputeWinnersAsync(context.CancellationToken).ConfigureAwait(false);
        return BuildResponse(summary);
    }

    /// <summary>
    /// Runs an ingestion cycle and assembles the resulting response, including the fresh price count,
    /// per-source outcomes, the updated schedule, and the current source list.
    /// </summary>
    private async Task<Contract.RefreshPriceSourcesResponse> RunCycleAndBuildResponseAsync(
        CancellationToken cancellationToken)
    {
        var summary = await _ingestionService.RunCycleAsync(cancellationToken).ConfigureAwait(false);
        return BuildResponse(summary);
    }

    /// <summary>
    /// Assembles a <see cref="Contract.RefreshPriceSourcesResponse"/> from an ingestion or recompute summary,
    /// including the fresh price count, per-source outcomes (empty for a recompute - no fetch occurred), the
    /// current schedule, and the current source list.
    /// </summary>
    private Contract.RefreshPriceSourcesResponse BuildResponse(IngestionCycleSummary summary)
    {
        var response = new Contract.RefreshPriceSourcesResponse
        {
            FreshPriceCount = summary.FreshPriceCount,

            // Built after the operation, so it carries whatever anchor is current: a live pull's own new
            // anchor, or - for a recompute, which never moves the anchor - the one already in place.
            Schedule = BuildSchedule()
        };

        foreach (var outcome in summary.Outcomes)
        {
            var wire = new Contract.PriceSourceOutcome
            {
                Source = outcome.Source,
                Succeeded = outcome.Succeeded,
                PriceCount = outcome.PriceCount
            };

            if (outcome.Error is not null) wire.Error = outcome.Error;

            response.Outcomes.Add(wire);
        }

        response.Sources.AddRange(BuildSources());
        return response;
    }

    /// <summary>Builds a list response containing the current schedule and the current source list.</summary>
    private Contract.ListPriceSourcesResponse BuildListResponse()
    {
        var response = new Contract.ListPriceSourcesResponse { Schedule = BuildSchedule() };
        response.Sources.AddRange(BuildSources());
        return response;
    }

    /// <summary>Builds the wire schedule from the current poll interval and the ingestion service's anchor.</summary>
    // The interval and its anchor, left for the client to add together. Two facts rather than a precomputed
    // next-pull instant: the panel counts down in the user's own clock, and shipping an absolute deadline
    // computed here would silently bake this machine's clock into it.
    private Contract.PriceSchedule BuildSchedule()
    {
        return new Contract.PriceSchedule
        {
            PollIntervalSeconds = (int)_pollInterval.TotalSeconds,
            ScheduleAnchorUtc = Timestamp.FromDateTimeOffset(_ingestionService.ScheduleAnchorUtc)
        };
    }

    /// <summary>Projects the toggle store's current source states into wire-format price sources.</summary>
    private IEnumerable<Contract.PriceSource> BuildSources()
    {
        return _toggleStore.List().Select(state => new Contract.PriceSource
        {
            Name = state.Name,
            Enabled = state.Enabled,
            PriorityScore = state.PriorityScore,
            PriceCount = state.PriceCount
        });
    }
}