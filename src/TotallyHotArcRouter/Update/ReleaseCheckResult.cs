namespace TotallyHot.ArcRouter.Update;

/// <summary>
/// Why a release check did not produce a definite "update available"/"up to date" answer. Carried on
/// <see cref="ReleaseCheckResult"/> rather than thrown, so <see cref="UpdateCheckHostedService"/> never
/// has to catch an exception out of a routine poll - a check that cannot resolve is itself a normal,
/// loggable outcome, not a failure of the poller.
/// </summary>
public enum ReleaseCheckUnavailableReason
{
    /// <summary>The check succeeded and produced a definite result; not itself an "unavailable" reason.</summary>
    None = 0,

    /// <summary>The repository has no GitHub Releases published yet.</summary>
    NoReleasesPublished,

    /// <summary>The release exists, but its <c>tag_name</c> was not a parseable <c>v&lt;version&gt;</c>.</summary>
    MalformedTag,

    /// <summary>
    /// The release exists and its tag parsed, but it does not publish both required pieces: exactly one
    /// recognizable <c>.msi</c> installer asset and a matching SHA256 checksum line for it (see
    /// <see cref="GitHubReleaseCheckClient"/>'s remarks for the checksum-publishing convention).
    /// </summary>
    AssetOrChecksumMissing,

    /// <summary>The GitHub API request failed - DNS, TLS, timeout, non-success status code, or malformed JSON.</summary>
    NetworkOrApiFailure
}

/// <summary>
/// The outcome of one <see cref="IReleaseCheckClient.CheckAsync"/> call: the running version, the latest
/// published version (when known), whether an update is available, and - only when an update is both
/// available and safely installable - the download URL and published SHA256 of the release's single MSI
/// installer asset. Every failure mode (no releases, a malformed tag, a missing asset/checksum, a
/// network failure) is represented as a typed, non-throwing result via <see cref="UnavailableReason"/>
/// rather than an exception, per the auto-update plan's "never throw out of the poller" requirement.
/// </summary>
/// <param name="CurrentVersion">
/// The running Router's own version (<c>Directory.Build.props</c>' <c>Version</c>, read from
/// <see cref="System.Reflection.AssemblyInformationalVersionAttribute"/>).
/// </param>
/// <param name="LatestVersion">
/// The latest published release's version, or <see langword="null"/> when it could not be
/// determined.
/// </param>
/// <param name="IsUpdateAvailable">
/// <see langword="true"/> only when <paramref name="LatestVersion"/> is strictly newer than
/// <paramref name="CurrentVersion"/> under <see cref="Version"/> ordering AND the release's MSI asset
/// plus its checksum were both found - an update that cannot actually be applied is never reported as
/// available.
/// </param>
/// <param name="AssetDownloadUrl">
/// The MSI installer asset's direct download URL, set only when
/// <paramref name="IsUpdateAvailable"/> is <see langword="true"/>.
/// </param>
/// <param name="AssetSha256">
/// The MSI asset's published SHA256 checksum (lowercase hex), set only when
/// <paramref name="IsUpdateAvailable"/> is <see langword="true"/>.
/// </param>
/// <param name="UnavailableReason">
/// Why the check could not produce a definite result;
/// <see cref="ReleaseCheckUnavailableReason.None"/> on a normal, definite outcome (available or not).
/// </param>
/// <param name="UnavailableDetail">
/// A human-readable elaboration of <paramref name="UnavailableReason"/>, for logs - never
/// thrown, always just carried.
/// </param>
public sealed record ReleaseCheckResult(
    string CurrentVersion,
    string? LatestVersion,
    bool IsUpdateAvailable,
    string? AssetDownloadUrl,
    string? AssetSha256,
    ReleaseCheckUnavailableReason UnavailableReason,
    string? UnavailableDetail)
{
    /// <summary>Builds a definite "no update available" (or "update available") result with no unavailable reason.</summary>
    /// <param name="currentVersion">The running Router's own version.</param>
    /// <param name="latestVersion">The latest published release's version.</param>
    /// <param name="isUpdateAvailable">Whether that release is both newer and fully installable.</param>
    /// <param name="assetDownloadUrl">The MSI's download URL, or <see langword="null"/> when no update is available.</param>
    /// <param name="assetSha256">The MSI's published SHA256, or <see langword="null"/> when no update is available.</param>
    public static ReleaseCheckResult Resolved(
        string currentVersion,
        string latestVersion,
        bool isUpdateAvailable,
        string? assetDownloadUrl,
        string? assetSha256)
    {
        return new ReleaseCheckResult(
            CurrentVersion: currentVersion,
            LatestVersion: latestVersion,
            IsUpdateAvailable: isUpdateAvailable,
            AssetDownloadUrl: assetDownloadUrl,
            AssetSha256: assetSha256,
            UnavailableReason: ReleaseCheckUnavailableReason.None,
            null);
    }

    /// <summary>Builds a typed "could not resolve" result - never an exception.</summary>
    /// <param name="currentVersion">The running Router's own version.</param>
    /// <param name="reason">Why the check could not produce a definite answer.</param>
    /// <param name="detail">A human-readable elaboration of <paramref name="reason"/>, for logs.</param>
    public static ReleaseCheckResult Unavailable(
        string currentVersion,
        ReleaseCheckUnavailableReason reason,
        string detail)
    {
        return new ReleaseCheckResult(CurrentVersion: currentVersion, null, false, null, null,
            UnavailableReason: reason,
            UnavailableDetail: detail);
    }
}