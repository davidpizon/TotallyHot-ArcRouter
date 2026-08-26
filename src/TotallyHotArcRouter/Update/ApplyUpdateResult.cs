namespace TotallyHot.ArcRouter.Update;

/// <summary>
/// The outcome of one <see cref="IUpdateApplier.ApplyAsync"/> call. A successful handoff does not mean
/// the update finished installing - it means <c>Updater.exe</c> was launched and will stop this service,
/// swap the install directory, and restart it; this process cannot observe its own death, so nothing past
/// the handoff is reported here (docs/router/auto-update-plan.md Phase 2).
/// </summary>
/// <param name="Succeeded">Whether the download, checksum verification, and <c>Updater.exe</c> launch all succeeded.</param>
/// <param name="Message">A human-readable outcome, for the GUI and the audit log.</param>
public sealed record ApplyUpdateResult(bool Succeeded, string Message)
{
    /// <summary>Builds a successful handoff result.</summary>
    public static ApplyUpdateResult Handoff(string message) => new(true, message);

    /// <summary>Builds a failure result - the download or checksum failed, or the updater could not be launched. Nothing was touched.</summary>
    public static ApplyUpdateResult Failure(string message) => new(false, message);
}
