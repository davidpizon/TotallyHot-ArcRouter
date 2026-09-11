namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Thrown when a gRPC admin call to the router fails. Carries a message fit to render in a Governance panel,
/// the System Settings window, or a tray notification rather than a raw <see cref="Grpc.Core.RpcException"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately one concrete type rather than a per-service hierarchy. Every admin client used to
/// declare its own empty subclass (<c>PriceSourceAdminException</c>, <c>ClusterModelAdminException</c>,
/// and ten more) that added no state and no behavior, and each was caught in exactly one place — so the type
/// carried no information the message and <see cref="IsUnavailable"/> did not already carry. See
/// <see href="../../../docs/adr/0010-collapse-the-per-feature-admin-slice-onto-shared-seams.md">ADR-0010</see>.
/// </para>
/// <para>
/// <c>ProviderAdminException</c> in <c>TotallyHot.ArcRouter.Gui.Admin</c> is intentionally *not* part of this
/// hierarchy: it belongs to the HTTP provider-admin client, which ADR-0007 decided stays on HTTP. Keeping the
/// two exception types unrelated keeps that transport split visible in the type system.
/// </para>
/// </remarks>
public sealed class GrpcAdminException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="GrpcAdminException"/> class.</summary>
    /// <param name="message">A plain-language description of the failure.</param>
    /// <param name="innerException">The underlying <see cref="Grpc.Core.RpcException"/>, if any.</param>
    /// <param name="isUnavailable">Whether the failure was specifically the router being unreachable.</param>
    public GrpcAdminException(string message, Exception? innerException = null, bool isUnavailable = false)
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
