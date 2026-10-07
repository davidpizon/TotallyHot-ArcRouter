using Grpc.Core;

namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// Signals that a passkey-gate limit was hit before any WebAuthn verification runs, so callers can map
/// the failure to a gRPC status without treating it as an unexpected fault. Challenge issuance uses
/// <see cref="StatusCode.ResourceExhausted"/> when the global token bucket is empty; other gate checks
/// typically surface through <see cref="TotallyHot.ArcRouter.Proxy.Auth.Passkey.ContentGate"/> as
/// <see cref="RpcException"/> instead.
/// </summary>
public sealed class PasskeyGateException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="PasskeyGateException"/> class.</summary>
    /// <param name="statusCode">The gRPC status callers should propagate.</param>
    /// <param name="detail">A safe, operator-facing explanation (never secret material).</param>
    public PasskeyGateException(StatusCode statusCode, string detail)
        : base(detail)
    {
        StatusCode = statusCode;
        Detail = detail;
    }

    /// <summary>Gets the gRPC status code for this refusal.</summary>
    public StatusCode StatusCode { get; }

    /// <summary>Gets the detail string (same as <see cref="Exception.Message"/>).</summary>
    public string Detail { get; }
}
