namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// The judge-calibration operations the Governance → Judge Calibration panel needs. An interface so
/// <c>JudgeCalibrationAdminStore</c> can be unit-tested against a fake without a live proxy or a gRPC
/// channel, mirroring <see cref="IRegretHarnessAdminClient"/>.
/// </summary>
/// <remarks>
/// One read and no mutation, unlike every other admin client here. The server recomputes on each call, so
/// there is no run to start and no state to set - the panel's Refresh button and its initial load are the
/// same operation.
/// </remarks>
public interface IJudgeCalibrationAdminClient
{
    /// <summary>Recomputes and reads the judge-vs-static calibration report over every shadow row.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="JudgeCalibrationAdminException">The call failed or the router is unreachable.</exception>
    Task<JudgeCalibrationReportInfo> GetReportAsync(CancellationToken cancellationToken = default);
}
