using Grpc.Core;

namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// Binding helpers for features that must sit behind the passkey content gate but are built outside #185:
/// the export/import RPCs of #165 and the per-turn text RPC (<c>GetTurnTexts</c>) of #176. Each helper
/// pairs a <see cref="ContentGate"/> check with the matching <see cref="GatedOperation"/> constant and
/// parameter binding, so those features call one method instead of re-deriving the operation name and
/// parameters string - a mismatch there would make the dashboard's approval unusable for the RPC.
/// </summary>
/// <remarks>
/// <para>
/// <b>Export (#165).</b> The dashboard runs <c>BeginOneOperation</c>/<c>FinishOneOperation</c> with
/// <see cref="GatedOperation.Export"/> and <see cref="GatedOperation.ExportParameters"/>, then passes the
/// resulting token in the export request; the RPC calls <see cref="RequireExport"/>.
/// </para>
/// <para>
/// <b>Import (#165).</b> The approval is bound to the SHA-256 of the staged archive
/// (<see cref="GatedOperation.ImportParameters"/>), so the approved bytes are the ones imported. The RPC
/// calls <see cref="RequireImport"/> with the digest it computed itself, never one the client supplied.
/// </para>
/// <para>
/// <b>Turn texts (#176).</b> Reading full prompt/response text is a read, not a mutation, so it needs the
/// ambient content grant rather than a one-operation token: the RPC calls
/// <see cref="RequireTurnTexts"/> with its <see cref="ServerCallContext"/>.
/// </para>
/// </remarks>
public static class ContentGateHooks
{
    /// <summary>
    /// Consumes a one-operation authorization for <see cref="GatedOperation.Export"/>.
    /// </summary>
    /// <param name="gate">The router's content gate.</param>
    /// <param name="authorizationToken">The token from the export request.</param>
    /// <exception cref="RpcException">When no passkey is enrolled or the token is missing, spent, or mismatched.</exception>
    public static void RequireExport(ContentGate gate, string? authorizationToken)
    {
        ArgumentNullException.ThrowIfNull(gate);
        gate.RequireAndConsumeOneOperation(
            authorizationToken, GatedOperation.Export, GatedOperation.ExportParameters());
    }

    /// <summary>
    /// Consumes a one-operation authorization for <see cref="GatedOperation.Import"/> bound to the staged
    /// archive's digest.
    /// </summary>
    /// <param name="gate">The router's content gate.</param>
    /// <param name="authorizationToken">The token from the import request.</param>
    /// <param name="stagedArchiveSha256Hex">Lowercase hex SHA-256 of the staged archive, computed by the router.</param>
    /// <exception cref="RpcException">When no passkey is enrolled or the token is missing, spent, or mismatched.</exception>
    public static void RequireImport(ContentGate gate, string? authorizationToken, string stagedArchiveSha256Hex)
    {
        ArgumentNullException.ThrowIfNull(gate);
        gate.RequireAndConsumeOneOperation(
            authorizationToken, GatedOperation.Import, GatedOperation.ImportParameters(stagedArchiveSha256Hex));
    }

    /// <summary>
    /// Requires a valid content grant on <paramref name="context"/> before full turn text is returned.
    /// </summary>
    /// <param name="gate">The router's content gate.</param>
    /// <param name="context">The call whose <c>x-content-grant</c> header is checked.</param>
    /// <exception cref="RpcException">When no passkey is enrolled or the grant is missing or expired.</exception>
    public static void RequireTurnTexts(ContentGate gate, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(gate);
        gate.RequireContentGrant(context);
    }
}
