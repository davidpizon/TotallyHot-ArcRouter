using System.Reflection;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// The GUI's own version string, as shown in the System Settings window's footer beside the Router's.
/// </summary>
/// <remarks>
/// Read from <see cref="AssemblyInformationalVersionAttribute"/> rather than the assembly's numeric
/// version because that attribute carries <c>Directory.Build.props</c>' shared <c>&lt;Version&gt;</c>
/// verbatim - the same value the MSI's ProductVersion and the Router's own reported version derive
/// from. That shared origin is what makes the footer's two halves comparable at a glance: when they
/// disagree, an upgrade genuinely did not land on both halves. The strip-at-'+' step mirrors
/// <c>GitHubReleaseCheckClient</c>'s, which needs it for the same reason - the SDK appends
/// <c>+&lt;git-sha&gt;</c> in a git checkout, which is noise in a UI label.
/// </remarks>
public static class AppVersion
{
    /// <summary>The version reported when the informational-version attribute is missing altogether.</summary>
    public const string Unknown = "0.0.0";

    /// <summary>The running GUI's version, e.g. <c>1.0.2</c>, with any build-metadata suffix removed.</summary>
    public static string Current { get; } = Read(typeof(AppVersion).Assembly);

    /// <summary>
    /// Reads <paramref name="assembly"/>'s informational version, stripped of build metadata. Takes the
    /// assembly as a parameter so tests can exercise it against something other than the one running
    /// them, whose version they cannot vary.
    /// </summary>
    /// <param name="assembly">The assembly whose version to read.</param>
    /// <returns>The plain version, or <see cref="Unknown"/> when the attribute is absent or blank.</returns>
    public static string Read(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return Strip(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    }

    /// <summary>
    /// Removes the <c>+&lt;git-sha&gt;</c> build metadata the SDK appends to an informational version,
    /// leaving the plain <c>major.minor.patch</c> the UI displays.
    /// </summary>
    /// <param name="informationalVersion">The raw informational version, which may be null or blank.</param>
    /// <returns>The version without build metadata, or <see cref="Unknown"/> when there is nothing to strip.</returns>
    public static string Strip(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return Unknown;
        }

        var plusIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plusIndex >= 0 ? informationalVersion[..plusIndex] : informationalVersion;
    }
}
