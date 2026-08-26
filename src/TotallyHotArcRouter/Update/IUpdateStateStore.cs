namespace TotallyHot.ArcRouter.Update;

/// <summary>
/// The last-known outcome of a release check, and when it was recorded. A read-model snapshot for
/// <c>UpdateAdminGrpcService.GetUpdateStatus</c> - ephemeral, in-memory, operational state rather than
/// data that needs to survive a restart (a restarted Router simply checks again).
/// </summary>
/// <param name="Result">The most recent <see cref="ReleaseCheckResult"/>, or <see langword="null"/> before the first check has completed.</param>
/// <param name="CheckedAtUtc">When <paramref name="Result"/> was recorded, or <see langword="null"/> before the first check.</param>
public sealed record UpdateStateSnapshot(ReleaseCheckResult? Result, DateTimeOffset? CheckedAtUtc);

/// <summary>
/// Holds the Router's last-known update-check outcome, shared between <see cref="UpdateCheckHostedService"/>
/// (the writer) and <see cref="UpdateAdminGrpcService"/> (the reader the GUI polls). In-memory only - see
/// <see cref="UpdateStateSnapshot"/>'s remarks for why persistence is unnecessary here.
/// </summary>
public interface IUpdateStateStore
{
    /// <summary>Gets the current snapshot. Never <see langword="null"/>; before the first check, both members of the snapshot are <see langword="null"/>.</summary>
    UpdateStateSnapshot Current { get; }

    /// <summary>Records a fresh check outcome, timestamped now.</summary>
    void Record(ReleaseCheckResult result);
}
