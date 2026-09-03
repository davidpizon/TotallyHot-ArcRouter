using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Thrown when a router-settings read or write call fails. Carries a message fit to render in the System
/// Settings window rather than a raw <see cref="RpcException"/>, mirroring <see cref="ClusterModelAdminException"/>.
/// See <see cref="GrpcAdminException.IsUnavailable"/>'s remarks.
/// </summary>
public sealed class RouterSettingsAdminException : GrpcAdminException
{
    /// <summary>Initializes a new instance of the <see cref="RouterSettingsAdminException"/> class.</summary>
    public RouterSettingsAdminException(string message, Exception? innerException = null, bool isUnavailable = false)
        : base(message: message, innerException: innerException, isUnavailable: isUnavailable)
    {
    }
}

/// <summary>
/// The router settings' currently effective values (docs/router/self-organizing-classification-plan.md
/// Phase T6; docs/router/geval-shadow-scoring-plan.md), as read or written by the System Settings window's
/// Adaptive Routing and Shadow Judge rows.
/// </summary>
/// <param name="AdaptiveRoutingEnabled">
/// Whether adaptive routing's transcript capture, cluster retraining, and
/// <c>cluster_best</c> voter are enabled.
/// </param>
/// <param name="EmbeddingMemoryCapacity">
/// The maximum number of embedding-memory entries retained before oldest-first
/// eviction.
/// </param>
/// <param name="JudgeEnabled">
/// Whether the G-Eval shadow judge is enabled. Also governs whether raw response text is
/// retained in memory for judging.
/// </param>
/// <param name="JudgeModelName">
/// The operator's chosen judge backbone, as a client-facing model name; empty means automatic (the first
/// eligible free model). This is the stored setting, not necessarily what will run - a pick that stops
/// being eligible is substituted at call time without the setting changing.
/// </param>
/// <param name="EligibleJudgeModels">
/// Every model currently able to serve as the judge backbone, in configuration order. Empty when no free
/// provider is configured, which is the honest "the judge has nothing to call" state rather than an error.
/// </param>
/// <param name="TranscriptCaptureEnabled">Whether the opt-in transcript store currently captures raw prompt/response text.</param>
public sealed record RouterSettingsInfo(
    bool AdaptiveRoutingEnabled,
    int EmbeddingMemoryCapacity,
    bool JudgeEnabled,
    string JudgeModelName,
    IReadOnlyList<string> EligibleJudgeModels,
    bool TranscriptCaptureEnabled);

/// <summary>
/// Client for the proxy's <c>RouterSettingsAdminService</c> - the System Settings window's Adaptive Routing,
/// Shadow Judge, and Transcription Capture read and write surface. Lives in this plain <c>net10.0</c> library
/// rather than the Windows-only MAUI project so CI can unit-test it, exactly like <see cref="ClusterModelAdminClient"/>.
/// </summary>
public sealed class RouterSettingsAdminClient
    : GrpcAdminClientBase<Contract.RouterSettingsAdminService.RouterSettingsAdminServiceClient,
            RouterSettingsAdminException>,
        IRouterSettingsAdminClient
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RouterSettingsAdminClient"/> class, creating and owning a
    /// channel to <paramref name="serverAddress"/>.
    /// </summary>
    public RouterSettingsAdminClient(string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
        : base(serverAddress: serverAddress,
            createClient: callInvoker =>
                new Contract.RouterSettingsAdminService.RouterSettingsAdminServiceClient(callInvoker))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RouterSettingsAdminClient"/> class over a caller-supplied
    /// generated client. The seam tests use to substitute a fake without a live server; the caller owns the
    /// channel's lifetime.
    /// </summary>
    public RouterSettingsAdminClient(Contract.RouterSettingsAdminService.RouterSettingsAdminServiceClient client)
        : base(client)
    {
    }

    /// <inheritdoc/>
    public async Task<RouterSettingsInfo> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
                .GetRouterSettingsAsync(request: new Contract.GetRouterSettingsRequest(),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return Map(response);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex: ex, action: "Could not read the router settings");
        }
    }

    /// <inheritdoc/>
    public async Task<RouterSettingsInfo> UpdateAsync(
        bool adaptiveRoutingEnabled,
        int embeddingMemoryCapacity,
        bool judgeEnabled,
        string judgeModelName,
        bool transcriptCaptureEnabled,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
                .UpdateRouterSettingsAsync(
                    request: new Contract.UpdateRouterSettingsRequest
                    {
                        AdaptiveRoutingEnabled = adaptiveRoutingEnabled,
                        EmbeddingMemoryCapacity = embeddingMemoryCapacity,
                        JudgeEnabled = judgeEnabled,
                        JudgeModelName = judgeModelName ?? string.Empty,
                        TranscriptCaptureEnabled = transcriptCaptureEnabled
                    },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return Map(response);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex: ex, action: "Could not save the router settings");
        }
    }

    /// <inheritdoc/>
    public async Task<int> ClearTranscriptsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
                .ClearTranscriptsAsync(request: new Contract.ClearTranscriptsRequest(),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return response.RowsDeleted;
        }
        catch (RpcException ex)
        {
            throw Wrap(ex: ex, action: "Could not clear the transcript data");
        }
    }

    /// <summary>Converts a gRPC-contract response into the client's <see cref="RouterSettingsInfo"/>.</summary>
    private static RouterSettingsInfo Map(Contract.RouterSettingsResponse response)
    {
        return new RouterSettingsInfo(
            AdaptiveRoutingEnabled: response.AdaptiveRoutingEnabled,
            EmbeddingMemoryCapacity: response.EmbeddingMemoryCapacity,
            JudgeEnabled: response.JudgeEnabled,
            JudgeModelName: response.JudgeModelName,
            EligibleJudgeModels: response.EligibleJudgeModels.ToList(),
            TranscriptCaptureEnabled: response.TranscriptCaptureEnabled);
    }

    /// <inheritdoc/>
    protected override RouterSettingsAdminException CreateException(string message, Exception? innerException,
        bool isUnavailable)
    {
        return new RouterSettingsAdminException(message: message, innerException: innerException,
            isUnavailable: isUnavailable);
    }
}