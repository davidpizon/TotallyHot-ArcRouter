using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Update;

/// <summary>
/// Configures the Router's background self-update checker (docs/router/auto-update-plan.md Phase 2),
/// bound from the <c>Update</c> section. Controls only whether/how often the Router polls GitHub
/// Releases for a newer version - it never authorizes an unattended apply; applying an update always
/// requires an explicit operator action from the GUI.
/// </summary>
public sealed class UpdateOptions
{
    /// <summary>Gets the configuration section name used for auto-update settings.</summary>
    public const string SectionName = "Update";

    /// <summary>
    /// Gets whether <see cref="UpdateCheckHostedService"/> polls for updates at all. Defaults to
    /// <see langword="true"/>. Even when enabled, this only ever detects an available update and
    /// records it in <see cref="IUpdateStateStore"/> - it never applies one.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Gets how often the background poller checks GitHub Releases for a newer version. Defaults to
    /// 6 hours - frequent enough that an operator sees a fresh release within a work day, infrequent
    /// enough to stay well clear of GitHub's unauthenticated rate limit (60 requests/hour/IP).
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "24:00:00")]
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Gets the GitHub API base URL <see cref="GitHubReleaseCheckClient"/> calls against. Defaults to
    /// the real API; overridable so tests (and, in principle, a GitHub Enterprise deployment) can point
    /// it elsewhere.
    /// </summary>
    [Required]
    public string GitHubApiBaseUrl { get; init; } = "https://api.github.com";

    /// <summary>
    /// Gets the Windows Service name <c>Updater.exe</c> stops and restarts around the file swap, matching
    /// <c>Program.cs</c>'s <c>UseWindowsService(options => options.ServiceName = "TotallyHotArcRouter")</c>
    /// call and <c>Install-RouterService.ps1</c>.
    /// </summary>
    [Required]
    public string ServiceName { get; init; } = "TotallyHotArcRouter";

    /// <summary>
    /// Performs domain-level validation that is not fully expressible through data annotations.
    /// </summary>
    /// <exception cref="OptionsValidationException">Thrown when the configuration is inconsistent.</exception>
    public void EnsureValid()
    {
        var errors = new List<string>();

        if (!Uri.TryCreate(GitHubApiBaseUrl, UriKind.Absolute, out _))
        {
            errors.Add($"GitHubApiBaseUrl '{GitHubApiBaseUrl}' must be an absolute URI.");
        }

        if (PollInterval <= TimeSpan.Zero)
        {
            errors.Add("PollInterval must be a positive duration.");
        }

        if (errors.Count > 0)
        {
            throw new OptionsValidationException(nameof(UpdateOptions), typeof(UpdateOptions), errors);
        }
    }
}
