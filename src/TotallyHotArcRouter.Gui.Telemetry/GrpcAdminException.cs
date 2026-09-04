namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Base for the per-service exceptions (<see cref="PriceSourceAdminException"/>,
/// <see cref="ClusterModelAdminException"/>, <see cref="BenchmarkDataAdminException"/>, etc.) thrown when a
/// gRPC admin call to the router fails. Carries a message fit to render in a Governance panel, the System
/// Settings window, or a tray notification rather than a raw <see cref="Grpc.Core.RpcException"/>.
/// </summary>
public abstract class GrpcAdminException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="GrpcAdminException"/> class.</summary>
    /// <param name="message">A plain-language description of the failure.</param>
    /// <param name="innerException">The underlying <see cref="Grpc.Core.RpcException"/>, if any.</param>
    /// <param name="isUnavailable">Whether the failure was specifically the router being unreachable.</param>
    protected GrpcAdminException(string message, Exception? innerException, bool isUnavailable)
        : base(message: message, innerException: innerException)
    {
        IsUnavailable = isUnavailable;
    }

    /// <summary>
    /// Gets whether the call failed because the router could not be reached, as opposed to being rejected by
    /// it. The distinction is load-bearing for the caller: "the router is down" is a fact about the whole
    /// connection and should put the panel into its unreachable state; a single rejected request (e.g. an
    /// out-of-range value, or "no price source named X") is a fact about one call and must not, or a single
    /// bad argument would blank a panel whose data is perfectly good.
    /// </summary>
    public bool IsUnavailable { get; }
}