namespace TotallyHot.ArcRouter.Updater;

/// <summary>
/// The Router-supplied instructions for one update swap: where the current install lives, the release
/// zip to install and the SHA256 it must hash to, the Windows Service name to stop/start around the
/// swap, and the caller's own process id to wait on before touching any files
/// (docs/router/auto-update-plan.md Phase 2's <c>Updater.exe</c> handoff contract).
/// </summary>
/// <remarks>
/// <paramref name="ExpectedSha256"/> is what makes this a real trust boundary rather than a
/// do-as-you-are-told file mover: <c>Updater.exe</c> runs with enough privilege to write into
/// <c>%ProgramFiles%</c> and restart a Windows Service, so it re-hashes <paramref name="ZipPath"/> itself
/// instead of trusting that whoever invoked it already did. The Router's own pre-launch verification in
/// <c>UpdateApplier</c> is a deliberate *duplicate* of this check, not a replacement for it - see
/// <see cref="UpdaterService.RunAsync"/>'s remarks.
/// </remarks>
/// <param name="InstallDirectory">The Router's install directory (e.g. <c>%ProgramFiles%\TotallyHotArcRouter\Router\</c>) to back up and overwrite.</param>
/// <param name="ZipPath">The downloaded release zip to extract over <paramref name="InstallDirectory"/>, re-verified against <paramref name="ExpectedSha256"/> before anything is touched.</param>
/// <param name="ServiceName">The Windows Service name to stop before the swap and start again after it.</param>
/// <param name="WaitPid">The calling Router process's id; the swap does not begin until this process has exited.</param>
/// <param name="ExpectedSha256">The lowercase-hex SHA256 the file at <paramref name="ZipPath"/> must hash to; a mismatch aborts the swap before the service is stopped.</param>
public sealed record UpdaterArguments(string InstallDirectory, string ZipPath, string ServiceName, int WaitPid, string ExpectedSha256);

/// <summary>
/// Parses <c>Updater.exe</c>'s command-line arguments. A separate static class (not folded into
/// <see cref="Program"/>) so it is directly unit-testable without spawning a real process, mirroring
/// <c>TotallyHot.ArcRouter.Program</c>'s <c>ExtractFlag</c>/<c>ExtractModelArg</c> convention.
/// </summary>
public static class ArgumentParser
{
    /// <summary>The number of hex characters a SHA256 digest is written as, and therefore the exact length <c>--expected-sha256</c> must be.</summary>
    private const int Sha256HexLength = 64;

    /// <summary>
    /// Parses <c>--install-dir</c>, <c>--zip-path</c>, <c>--service-name</c>, <c>--wait-pid</c>, and
    /// <c>--expected-sha256</c> (each <c>&lt;flag&gt; &lt;value&gt;</c>, space-separated) out of
    /// <paramref name="args"/>. All five are required: both binaries always ship together, so there is no
    /// older caller to stay compatible with, and making the checksum optional would let a bad invocation
    /// silently skip the trust-boundary verification it exists to enforce.
    /// </summary>
    /// <exception cref="ArgumentException">One or more required arguments were missing, empty, not a valid integer (<c>--wait-pid</c>), or not 64 hex characters (<c>--expected-sha256</c>).</exception>
    public static UpdaterArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var installDirectory = ExtractOption(args, "--install-dir");
        var zipPath = ExtractOption(args, "--zip-path");
        var serviceName = ExtractOption(args, "--service-name");
        var waitPidText = ExtractOption(args, "--wait-pid");
        var expectedSha256 = ExtractOption(args, "--expected-sha256");

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            errors.Add("--install-dir is required.");
        }

        if (string.IsNullOrWhiteSpace(zipPath))
        {
            errors.Add("--zip-path is required.");
        }

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            errors.Add("--service-name is required.");
        }

        var waitPid = 0;
        if (string.IsNullOrWhiteSpace(waitPidText) || !int.TryParse(waitPidText, out waitPid))
        {
            errors.Add("--wait-pid is required and must be a valid integer process id.");
        }

        if (!IsSha256Hex(expectedSha256))
        {
            errors.Add("--expected-sha256 is required and must be 64 hexadecimal characters.");
        }

        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(args));
        }

        return new UpdaterArguments(installDirectory!, zipPath!, serviceName!, waitPid, expectedSha256!.ToLowerInvariant());
    }

    /// <summary>
    /// Whether <paramref name="value"/> is exactly 64 hexadecimal characters. Accepted in either case and
    /// normalized to lowercase by <see cref="Parse"/>, so an uppercase digest from a hand-run
    /// <c>Get-FileHash</c> is not rejected as malformed, while genuinely wrong-shaped input still is.
    /// </summary>
    private static bool IsSha256Hex(string? value)
    {
        if (value is null || value.Length != Sha256HexLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns the value following the first case-insensitive occurrence of <paramref name="name"/>, or <see langword="null"/> if absent or trailing.</summary>
    internal static string? ExtractOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
