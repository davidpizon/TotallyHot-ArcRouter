using TotallyHot.ArcRouter.Gui.Console;
using TotallyHot.ArcRouter.Gui.Models;
using TotallyHot.ArcRouter.Gui.Telemetry;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Connects to the TotallyHot.ArcRouter proxy's <c>TelemetryService.StreamEvents</c> gRPC RPC and maintains a
/// live, continuously re-aggregated view of routing telemetry as <see cref="Conversation"/> records -
/// the same shape <see cref="MockData"/> exposes, so dashboard components render live and mock data
/// identically. Replaces the former SignalR <c>HubConnection</c>-based implementation - see
/// docs/router/grpc-migration.md.
/// </summary>
/// <remarks>
/// Registered as a singleton in <c>MauiProgram</c> and started once from <c>Dashboard.razor</c>'s
/// <c>OnInitializedAsync</c>. Most of its state-mutation logic is unit-tested against a fake
/// <see cref="Contract.TelemetryService.TelemetryServiceClient"/> stream; only the actual
/// <see cref="GrpcChannel"/> networking requires a live server and stays untested in-process. The
/// logic it delegates to - <see cref="ConversationAggregator.Aggregate"/> and
/// <see cref="LiveConversationMapper.ToModel(LiveConversation)"/> - is separately unit-tested (the
/// former in TotallyHot.ArcRouter.Gui.Telemetry.Tests). The generated
/// <see cref="Contract.TelemetryService.TelemetryServiceClient"/> is compiled into
/// TotallyHot.ArcRouter.Gui.Telemetry (not this project) and reaches here via that project's
/// <c>ProjectReference</c> - .NET MAUI's <c>SingleProject</c> build doesn't reliably run Grpc.Tools'
/// codegen, so the .proto is compiled in that plain, non-MAUI sibling instead (see its own csproj
/// comment for the full story). <see cref="MapToDto(Contract.RoutingTelemetryEvent)"/> and
/// <see cref="MapToDto(Contract.LogLineEvent)"/> below convert the generated wire types into the
/// existing <see cref="RoutingTelemetryEventDto"/>/<see cref="LogLineDto"/> shapes
/// TotallyHot.ArcRouter.Gui.Telemetry/.Console already consume, so nothing downstream of this class changed.
/// </remarks>
public sealed class LiveDataStore : IAsyncDisposable
{
    /// <summary>
    /// Default gRPC server address - <c>TotallyHot.ArcRouter.Proxy.ProxyServer.DefaultGrpcPort</c> (5002), a
    /// dedicated TLS port separate from the plain-HTTP proxy port (5001). Not currently configurable
    /// from the GUI - there is no existing settings storage mechanism to persist a custom address;
    /// see docs/gui/dashboard.md for this known limitation. HTTPS, not HTTP: see the constructor's
    /// remarks on why this moved off unencrypted h2c.
    /// </summary>
    public const string DefaultServerAddress = TelemetryChannelFactory.DefaultServerAddress;

    /// <summary>Fixed delay between reconnect attempts. See "Known gap: no built-in reconnect" in docs/router/grpc-migration.md.</summary>
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    private readonly object _lock = new();
    private readonly List<RoutingTelemetryEventDto> _events = [];
    private readonly LogBuffer _logBuffer = new();
    private readonly GrpcChannel _channel;
    private readonly Contract.TelemetryService.TelemetryServiceClient _client;
    private readonly ILogger<LiveDataStore>? _logger;
    private readonly string _serverAddress;

    private CancellationTokenSource? _streamCts;
    private volatile IReadOnlyList<Conversation> _conversations = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveDataStore"/> class.
    /// </summary>
    /// <remarks>
    /// Connects over HTTPS/2 (ALPN-negotiated), not unencrypted h2c: an earlier version of this class
    /// used unencrypted HTTP/2 (matching <c>TotallyHot.ArcRouter.Proxy.ProxyServer</c>'s original design in
    /// docs/router/grpc-migration.md), but that turned out to be unreliable on at least one
    /// managed/corporate Windows machine - every connection attempt failed with the HTTP/2-level
    /// <c>HTTP_1_1_REQUIRED</c> error, consistent with something on the network path (VPN client,
    /// endpoint security agent, TLS-inspecting proxy) not understanding or mangling the h2c connection
    /// preface, even on loopback. Real TLS + ALPN is far less likely to be interfered with. The
    /// server's certificate is self-signed (<c>TotallyHot.ArcRouter.Telemetry.TelemetryTlsCertificate</c> on
    /// the proxy side) - <see cref="TelemetryChannelFactory.ValidateLoopbackCertificate"/> trusts it based
    /// on subject name (<c>CN=localhost</c>), not chain validation, since both processes run as the
    /// same OS user on the same machine and there's no CA to issue a "real" certificate for a loopback
    /// address anyway.
    /// </remarks>
    public LiveDataStore(ILogger<LiveDataStore>? logger = null, string serverAddress = DefaultServerAddress)
    {
        _logger = logger;
        _serverAddress = serverAddress;

        // Channel construction and the self-signed-certificate trust decision live in
        // TelemetryChannelFactory, shared with PriceSourceAdminClient - both talk to the same process over
        // the same certificate, and two copies of a validation callback is how they drift apart.
        _channel = TelemetryChannelFactory.Create(serverAddress);
        _client = new Contract.TelemetryService.TelemetryServiceClient(TelemetryChannelFactory.Authenticated(_channel));
        WriteDiagnosticLog($"LiveDataStore constructed. serverAddress={serverAddress}");
    }

    /// <summary>
    /// Temporary diagnostic aid for the live-telemetry-not-arriving investigation: appends a
    /// timestamped line to a per-user log file, independent of <see cref="ILogger{TCategoryName}"/>
    /// (which only surfaces anywhere visible - the Debug Output window - when a debugger is attached,
    /// e.g. Visual Studio F5). Best-effort: a failure to write here must never affect the caller, so
    /// every failure mode is swallowed. Remove once the connection issue is root-caused.
    /// </summary>
    private static void WriteDiagnosticLog(string message)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TotallyHotArcRouter");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "gui-telemetry-debug.log"),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Best-effort diagnostic logging only - never let a failure to write it affect anything.
        }
    }

    /// <summary>Conversations reconstructed from telemetry received so far. Empty until the proxy sends events.</summary>
    public IReadOnlyList<Conversation> Conversations => _conversations;

    /// <summary>Log lines received so far for the Console tab, oldest first, bounded by <see cref="LogBuffer.Capacity"/>.</summary>
    public IReadOnlyList<LogLineDto> LogLines => _logBuffer.Snapshot();

    /// <summary>
    /// Raised after a new telemetry event has been folded in, on whatever thread the gRPC stream
    /// delivered the event on. Subscribers (Razor components) must marshal back to their own render
    /// context, e.g. via <c>ComponentBase.InvokeAsync(StateHasChanged)</c>.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Raised after <see cref="LogLines"/> changes (a line arrived, or the buffer was cleared), on
    /// whatever thread delivered the change. Kept separate from <see cref="Changed"/> so the Console
    /// tab can re-render on every log line without forcing every other tab to re-render too.
    /// </summary>
    public event Action? LogLinesChanged;

    /// <summary>
    /// Starts consuming the proxy's telemetry stream in the background. Connection failures (e.g. the
    /// proxy isn't running yet) are logged and swallowed - the dashboard simply shows no live
    /// conversations until a connection succeeds; the background loop keeps retrying with a fixed
    /// delay (<see cref="ReconnectDelay"/>) since, unlike SignalR's <c>WithAutomaticReconnect</c>,
    /// gRPC has no built-in reconnect policy - see docs/router/grpc-migration.md.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Guard against a second StartAsync call (e.g. a caller invoking it more than once):
        // without this, the previous _streamCts reference would be overwritten and lost, leaving
        // its ConsumeStreamWithReconnectAsync loop un-cancellable and running concurrently with the
        // new one - duplicate events and a leaked connection. Cancel (not Dispose) any previous one
        // first - the old loop's own in-flight gRPC call may still hold internal registrations
        // against that token, and disposing out from under it could throw ObjectDisposedException
        // there; Cancel() alone is enough to make it observe cancellation and exit.
        _streamCts?.Cancel();

        _streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        WriteDiagnosticLog("StartAsync called.");
        _ = ConsumeStreamWithReconnectAsync(_streamCts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs the long-lived gRPC telemetry stream loop: opens <c>StreamEvents</c>, dispatches each
    /// received event, and reconnects after <see cref="ReconnectDelay"/> if the stream drops, until
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    private async Task ConsumeStreamWithReconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    WriteDiagnosticLog($"Calling StreamEvents against {_serverAddress}...");
                    using var call = _client.StreamEvents(new Contract.StreamEventsRequest(), cancellationToken: cancellationToken);
                    WriteDiagnosticLog("StreamEvents call started; awaiting first event.");
                    await foreach (var telemetryEvent in call.ResponseStream.ReadAllAsync(cancellationToken))
                    {
                        WriteDiagnosticLog($"Received event: {telemetryEvent.EventCase}.");

                        // Isolated per-event: a single malformed message (e.g. a cost string that
                        // fails decimal.Parse) must not tear down and reconnect the whole stream -
                        // it just skips that one event and keeps reading the next.
                        try
                        {
                            Dispatch(telemetryEvent);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Failed to process a telemetry event; skipping it.");
                            WriteDiagnosticLog($"Failed to process event: {ex.GetType().Name}: {ex.Message}");
                        }
                    }

                    WriteDiagnosticLog("StreamEvents call ended (server completed the call).");
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // Not just RpcException: an h2c/HTTP-2 negotiation failure, DNS resolution
                    // failure, or anything else Grpc.Net.Client's transport can throw would
                    // otherwise escape uncaught here, silently killing this fire-and-forget loop on
                    // the very first attempt - no retry, no log, and the dashboard just stays on
                    // "waiting for data" forever with no indication anything went wrong.
                    _logger?.LogWarning(
                        ex,
                        "Telemetry stream to {ServerAddress} disconnected ({ExceptionType}: {ExceptionMessage}); retrying in {DelaySeconds}s.",
                        _serverAddress,
                        ex.GetType().Name,
                        ex.Message,
                        ReconnectDelay.TotalSeconds);
                    WriteDiagnosticLog($"StreamEvents failed: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex}");
                    await Task.Delay(ReconnectDelay, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on DisposeAsync - not a connection failure worth logging.
        }
    }

    /// <summary>Routes a decoded telemetry event to the appropriate handler based on its oneof case.</summary>
    private void Dispatch(Contract.TelemetryEvent telemetryEvent)
    {
        switch (telemetryEvent.EventCase)
        {
            case Contract.TelemetryEvent.EventOneofCase.RoutingTelemetry:
                OnRoutingTelemetryReceived(MapToDto(telemetryEvent.RoutingTelemetry));
                break;
            case Contract.TelemetryEvent.EventOneofCase.LogLine:
                OnLogLineReceived(MapToDto(telemetryEvent.LogLine));
                break;
            case Contract.TelemetryEvent.EventOneofCase.None:
            default:
                break;
        }
    }

    /// <summary>Converts a gRPC-contract <see cref="Contract.RoutingTelemetryEvent"/> into the store's <see cref="RoutingTelemetryEventDto"/>.</summary>
    private static RoutingTelemetryEventDto MapToDto(Contract.RoutingTelemetryEvent e) => new(
        SessionId: e.SessionId,
        TurnNumber: e.TurnNumber,
        IsSessionSynthesized: e.IsSessionSynthesized,
        RequestedModel: e.RequestedModel,
        ResolvedModel: e.ResolvedModel,
        RoutedModel: e.RoutedModel,
        Provider: e.Provider,
        IsFallback: e.IsFallback,
        PromptTokens: e.HasPromptTokens ? e.PromptTokens : null,
        CompletionTokens: e.HasCompletionTokens ? e.CompletionTokens : null,
        // Decimal-as-string, not double: see "Decimal encoding" in docs/router/grpc-migration.md.
        EstimatedCostUsd: e.HasEstimatedCostUsd ? decimal.Parse(e.EstimatedCostUsd, System.Globalization.CultureInfo.InvariantCulture) : null,
        IsStreaming: e.IsStreaming,
        LatencyToHeadersMs: e.LatencyToHeadersMs,
        TotalDurationMs: e.TotalDurationMs,
        StatusCode: e.StatusCode,
        TimestampUtc: e.TimestampUtc.ToDateTimeOffset(),
        CacheCreationTokens: e.HasCacheCreationTokens ? e.CacheCreationTokens : null,
        CacheReadTokens: e.HasCacheReadTokens ? e.CacheReadTokens : null,
        RequestSummary: e.HasRequestSummary ? e.RequestSummary : null,
        ResponseSummary: e.HasResponseSummary ? e.ResponseSummary : null,
        CostConfidence: e.HasCostConfidence ? e.CostConfidence : null,
        // Absent means an older proxy that predates router-cost accounting, which is reported as zero
        // overhead rather than as an unknown - the same value a current proxy sends when it computed no
        // embedding for the request.
        RouterTokens: e.HasRouterTokens ? e.RouterTokens : 0,
        // TryParse, not Parse: a malformed value from a mixed-version or non-router writer on the
        // telemetry stream must degrade to 0m rather than take down live rendering for every event.
        RouterCostUsd: e.HasRouterCostUsd && decimal.TryParse(e.RouterCostUsd, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var routerCostUsd)
            ? routerCostUsd
            : 0m,
        // Plain (not optional) proto3 field, like RoutedModel above: an older writer that predates this
        // phase sends neither, decoding as "" - degrade that to null rather than a fabricated reason.
        SubstitutionReason: string.IsNullOrEmpty(e.SubstitutionReason) ? null : e.SubstitutionReason);

    /// <summary>Converts a gRPC-contract <see cref="Contract.LogLineEvent"/> into the store's <see cref="LogLineDto"/>.</summary>
    private static LogLineDto MapToDto(Contract.LogLineEvent e) => new(
        e.TimestampUtc.ToDateTimeOffset(),
        e.Level,
        e.Message);

    /// <summary>Appends a routing telemetry event, rebuilds the aggregated conversation list, and raises <see cref="Changed"/>.</summary>
    private void OnRoutingTelemetryReceived(RoutingTelemetryEventDto dto)
    {
        lock (_lock)
        {
            _events.Add(dto);
            _conversations = ConversationAggregator.Aggregate(_events)
                .Select(LiveConversationMapper.ToModel)
                .ToList();
        }

        Changed?.Invoke();
    }

    /// <summary>Appends a log line to the buffer and raises <see cref="LogLinesChanged"/>.</summary>
    private void OnLogLineReceived(LogLineDto line)
    {
        _logBuffer.Add(line);
        LogLinesChanged?.Invoke();
    }

    /// <summary>Empties the log buffer. Implements the Console tab toolbar's "Clear Buffer" action.</summary>
    public void ClearLogLines()
    {
        _logBuffer.Clear();
        LogLinesChanged?.Invoke();
    }

    /// <summary>
    /// Empties accumulated telemetry events and the derived <see cref="Conversations"/> list. Implements
    /// the Settings modal's Reset Stats/Clear History actions - scoped to this session's live view, not
    /// the proxy's own durable history (<see cref="UsageStore"/> reads that separately, straight from
    /// the proxy, and is unaffected by this). Clears both fields under <see cref="_lock"/> alongside
    /// <see cref="OnRoutingTelemetryReceived"/> so a telemetry event racing with a reset can never leave
    /// <see cref="_events"/> and <see cref="Conversations"/> out of sync.
    /// </summary>
    public void ClearEvents()
    {
        lock (_lock)
        {
            _events.Clear();
            _conversations = [];
        }

        Changed?.Invoke();
    }

    /// <summary>Cancels the live telemetry stream and releases the gRPC channel.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_streamCts is not null)
        {
            await _streamCts.CancelAsync();
            _streamCts.Dispose();
        }

        // GrpcChannel (Grpc.Net.Client) doesn't expose ShutdownAsync - disposing is sufficient
        // to tear down active calls once the CTS is cancelled.
        _channel.Dispose();
    }
}

