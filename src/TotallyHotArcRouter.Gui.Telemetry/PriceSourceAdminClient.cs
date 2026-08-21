using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Thrown when a price-source management call fails. Carries a message fit to render in the Governance panel
/// rather than a raw <see cref="RpcException"/>, mirroring how <c>ProviderAdminException</c> wraps the
/// provider management API's failures.
/// </summary>
public sealed class PriceSourceAdminException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="PriceSourceAdminException"/> class.</summary>
    public PriceSourceAdminException(string message, Exception? innerException = null, bool isUnavailable = false)
        : base(message, innerException)
    {
        IsUnavailable = isUnavailable;
    }

    /// <summary>
    /// Gets whether the call failed because the router could not be reached, as opposed to being rejected by
    /// it.
    /// </summary>
    /// <remarks>
    /// The distinction is load-bearing for the caller, which is why it is a property rather than something to
    /// infer from <see cref="Exception.Message"/>. "The router is down" is a fact about the whole connection
    /// and should put the panel into its unreachable state; "no price source named X" is a fact about one
    /// request and must not, or a single bad argument would blank a panel whose data is perfectly good.
    /// </remarks>
    public bool IsUnavailable { get; }
}

/// <summary>
/// One price feed's status, as rendered by the Governance → Price Sources panel.
/// </summary>
/// <param name="Name">The source's registry name.</param>
/// <param name="Enabled">Whether the source is polled and served.</param>
/// <param name="PriorityScore">Rank arbitrating contested cells; higher wins. Reorderable via <see cref="IPriceSourceAdminClient.ReorderAsync"/>.</param>
/// <param name="PriceCount">How many price rows this source owns; 0 if it has never polled.</param>
public sealed record PriceSourceStatus(
    string Name,
    bool Enabled,
    int PriorityScore,
    int PriceCount);

/// <summary>
/// When the router will next pull prices of its own accord, as the two facts it reports rather than a
/// precomputed instant. Backs the Governance → Price Sources countdown.
/// </summary>
/// <param name="PollInterval">The catalog's poll cadence (4-12h).</param>
/// <param name="ScheduleAnchorUtc">
/// When the current interval started counting: the last cycle to complete, or the router's start if none
/// has. Every cycle re-anchors it - including a manual pull, and including a cycle in which every source
/// failed.
/// </param>
public sealed record PriceSourceSchedule(TimeSpan PollInterval, DateTimeOffset ScheduleAnchorUtc)
{
    /// <summary>Gets when the next scheduled pull is due. May be in the past if a cycle is late or running.</summary>
    public DateTimeOffset NextPullUtc => ScheduleAnchorUtc + PollInterval;
}

/// <summary>The source list and the schedule it sits under - what every read of this API returns.</summary>
/// <param name="Sources">Every known source's status, enabled or not.</param>
/// <param name="Schedule">When the next scheduled pull lands.</param>
public sealed record PriceSourceList(
    IReadOnlyList<PriceSourceStatus> Sources,
    PriceSourceSchedule Schedule);

/// <summary>One source's result from a refresh cycle.</summary>
/// <param name="Source">The source name.</param>
/// <param name="Succeeded">Whether the fetch and upsert completed.</param>
/// <param name="PriceCount">How many price rows were written.</param>
/// <param name="Error">Why it failed, when it did.</param>
public sealed record PriceRefreshOutcome(string Source, bool Succeeded, int PriceCount, string? Error);

/// <summary>The result of a manual pull.</summary>
/// <param name="Outcomes">Per-source results.</param>
/// <param name="FreshPriceCount">Price rows fresher than the 24h floor after the cycle.</param>
/// <param name="Sources">Every source's status after the cycle.</param>
/// <param name="Schedule">The schedule after the cycle, which the cycle itself just re-anchored.</param>
public sealed record PriceRefreshResult(
    IReadOnlyList<PriceRefreshOutcome> Outcomes,
    int FreshPriceCount,
    IReadOnlyList<PriceSourceStatus> Sources,
    PriceSourceSchedule Schedule);

/// <summary>
/// Client for the proxy's <c>PriceSourceAdminService</c> - the Governance → Price Sources panel's read and
/// mutate surface. Lives in this plain <c>net10.0</c> library rather than the Windows-only MAUI project so CI
/// can unit-test it, exactly like <c>ProviderAdminClient</c>.
/// </summary>
/// <remarks>
/// Carries feed metadata only, never prices (D5) - see the service comment in <c>src/Protos/telemetry.proto</c>.
/// </remarks>
public sealed class PriceSourceAdminClient : IPriceSourceAdminClient, IDisposable
{
    // Mirrors PriceCatalogOptions.PollIntervalHours' default, for the one case where a response carries no
    // schedule. Duplicated rather than shared because this library deliberately doesn't reference the router.
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromHours(6);

    private readonly Contract.PriceSourceAdminService.PriceSourceAdminServiceClient _client;
    private readonly IDisposable? _ownedChannel;

    /// <summary>
    /// Initializes a new instance of the <see cref="PriceSourceAdminClient"/> class, creating and owning a
    /// channel to <paramref name="serverAddress"/>.
    /// </summary>
    public PriceSourceAdminClient(string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
    {
        var channel = TelemetryChannelFactory.Create(serverAddress);
        _ownedChannel = channel;
        _client = new Contract.PriceSourceAdminService.PriceSourceAdminServiceClient(TelemetryChannelFactory.Authenticated(channel));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PriceSourceAdminClient"/> class over a caller-supplied
    /// generated client. The seam tests use to substitute a fake without a live server; the caller owns the
    /// channel's lifetime.
    /// </summary>
    public PriceSourceAdminClient(Contract.PriceSourceAdminService.PriceSourceAdminServiceClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _ownedChannel = null;
    }

    /// <inheritdoc />
    public async Task<PriceSourceList> ListAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client
                .ListPriceSourcesAsync(new Contract.ListPriceSourcesRequest(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return MapList(response.Sources, response.Schedule);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex, "Could not read the price sources");
        }
    }

    /// <inheritdoc />
    public async Task<PriceSourceList> SetEnabledAsync(
        string name,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            var response = await _client
                .SetPriceSourceEnabledAsync(
                    new Contract.SetPriceSourceEnabledRequest { Name = name, Enabled = enabled },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return MapList(response.Sources, response.Schedule);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex, $"Could not {(enabled ? "enable" : "disable")} '{name}'");
        }
    }

    /// <inheritdoc />
    public async Task<PriceRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client
                .RefreshPriceSourcesAsync(new Contract.RefreshPriceSourcesRequest(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return MapRefreshResult(response);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex, "Could not refresh the price sources");
        }
    }

    /// <inheritdoc />
    public async Task<PriceRefreshResult> ReorderAsync(
        IReadOnlyList<string> namesInPriorityOrder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(namesInPriorityOrder);

        var request = new Contract.ReorderPriceSourcesRequest();
        request.SourceNamesInPriorityOrder.AddRange(namesInPriorityOrder);

        try
        {
            var response = await _client
                .ReorderPriceSourcesAsync(request, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return MapRefreshResult(response);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex, "Could not reorder the price sources");
        }
    }

    // ReorderPriceSources returns the same wire shape as RefreshPriceSources because the client needs the
    // same information either way (updated sources, schedule, fresh count) - not because the same cycle ran.
    // A reorder recomputes from storage rather than pulling, so its Outcomes is always empty.
    /// <summary>Converts a gRPC-contract refresh/reorder response into the client's <see cref="PriceRefreshResult"/>.</summary>
    private static PriceRefreshResult MapRefreshResult(Contract.RefreshPriceSourcesResponse response)
    {
        var outcomes = response.Outcomes
            .Select(o => new PriceRefreshOutcome(
                o.Source,
                o.Succeeded,
                o.PriceCount,
                o.HasError ? o.Error : null))
            .ToList();

        return new PriceRefreshResult(
            outcomes,
            response.FreshPriceCount,
            MapSources(response.Sources),
            MapSchedule(response.Schedule));
    }

    /// <summary>Converts gRPC-contract sources and schedule into the client's <see cref="PriceSourceList"/>.</summary>
    private static PriceSourceList MapList(
        IEnumerable<Contract.PriceSource> sources,
        Contract.PriceSchedule? schedule) =>
        new(MapSources(sources), MapSchedule(schedule));

    /// <summary>Converts gRPC-contract price sources into the client's <see cref="PriceSourceStatus"/> list.</summary>
    private static IReadOnlyList<PriceSourceStatus> MapSources(IEnumerable<Contract.PriceSource> sources) =>
        [.. sources.Select(s => new PriceSourceStatus(
            s.Name,
            s.Enabled,
            s.PriorityScore,
            s.PriceCount))];

    // A message field is absent on the wire when unset, so an older router - or a test fake that doesn't
    // populate it - lands here as null. Substituting the configured default rather than throwing keeps the
    // panel's source list, the part that matters, rendering; the countdown is the only thing that would be
    // off, and only against a router predating the field.
    /// <summary>Converts a gRPC-contract schedule into the client's <see cref="PriceSourceSchedule"/>, substituting a default when the field is unset.</summary>
    private static PriceSourceSchedule MapSchedule(Contract.PriceSchedule? schedule) =>
        schedule is null
            ? new PriceSourceSchedule(DefaultPollInterval, DateTimeOffset.UtcNow)
            : new PriceSourceSchedule(
                TimeSpan.FromSeconds(schedule.PollIntervalSeconds),
                schedule.ScheduleAnchorUtc.ToDateTimeOffset());

    // Unavailable means the proxy isn't running, which is an ordinary state for a GUI that can outlive it -
    // so it gets a plain-language message rather than a gRPC status dump, and is flagged so the caller can
    // tell a dead connection from a rejected request without parsing the text. Everything else keeps the
    // server's own detail, which is where NotFound's "no price source named X" comes from.
    /// <summary>Wraps an <see cref="RpcException"/> into a <see cref="PriceSourceAdminException"/>, flagging router-unreachable errors distinctly.</summary>
    private static PriceSourceAdminException Wrap(RpcException ex, string action) =>
        ex.StatusCode == StatusCode.Unavailable
            ? new PriceSourceAdminException($"{action}: the router is not reachable.", ex, isUnavailable: true)
            : new PriceSourceAdminException($"{action}: {ex.Status.Detail}", ex);

    /// <inheritdoc />
    public void Dispose() => _ownedChannel?.Dispose();
}

