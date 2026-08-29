using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Builds the GUI's Serilog logger from its own <c>appsettings.json</c>, and guarantees that a logger
/// with at least one sink always comes back.
/// </summary>
/// <remarks>
/// <para>
/// The GUI shipped with no logging configured at all, so when the installed build's WebView2 environment
/// failed to initialize the dashboard window opened blank and nothing was written anywhere - the Router's
/// <c>C:\Logs\ArcRouter\arcrouter-.log</c> held only service entries, because that is a different process.
/// This type closes that gap, and its defensive shape is a direct response to that failure: a bootstrap
/// that silently produces a sink-less logger reproduces the same undiagnosable symptom, so both "the
/// configuration file is missing" and "the configuration file is unusable" fall back to a built-in
/// rolling file sink rather than to silence.
/// </para>
/// <para>
/// The log lives under <c>%LOCALAPPDATA%</c> rather than the Router's <c>C:\Logs\ArcRouter</c> because the
/// two processes run as different identities - the Router as LocalSystem under the service control
/// manager, the GUI as the interactive user - so they cannot share one file without one of them being
/// denied. That per-user root already holds <see cref="GuiSettingsStore"/>'s settings file and
/// <see cref="WebViewUserData"/>'s WebView2 folder.
/// </para>
/// </remarks>
public static class GuiLogging
{
    /// <summary>
    /// The configuration file read from the application's base directory. Named to match the Router's own
    /// file so an operator looking for "where do I change the log level" finds the same thing in both
    /// install folders.
    /// </summary>
    public const string ConfigurationFileName = "appsettings.json";

    /// <summary>
    /// The configuration path Serilog's sink array lives under. Used to discover configured file sinks
    /// without hardcoding their position in that array.
    /// </summary>
    private const string WriteToSection = "Serilog:WriteTo";

    /// <summary>
    /// The output template the built-in fallback sink uses. Matches the Router's configured template so
    /// the two logs read the same way when they are compared side by side during an incident.
    /// </summary>
    private const string FallbackOutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Creates the logger the GUI runs with: <see cref="ConfigurationFileName"/> from the application's
    /// base directory, with every configured file-sink path expanded, and a built-in sink substituted if
    /// that configuration is missing or unusable.
    /// </summary>
    /// <returns>A configured logger; never one without a sink.</returns>
    public static Logger CreateDefaultLogger() =>
        CreateLogger(BuildConfiguration(AppContext.BaseDirectory), FallbackLogPath());

    /// <summary>
    /// The built-in log path used when configuration names none:
    /// <c>%LOCALAPPDATA%\TotallyHotArcRouter\logs\arcrouter-gui-.log</c>. Resolved through
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> rather than the <c>LOCALAPPDATA</c>
    /// environment variable so it still resolves for an identity that has no such variable set.
    /// </summary>
    /// <returns>The absolute path of the fallback rolling log file.</returns>
    public static string FallbackLogPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TotallyHotArcRouter",
        "logs",
        "arcrouter-gui-.log");

    /// <summary>
    /// Reads <see cref="ConfigurationFileName"/> from <paramref name="basePath"/> and layers the expanded
    /// file-sink paths from <see cref="ResolveFileSinkPaths"/> on top of it.
    /// </summary>
    /// <param name="basePath">
    /// The directory to read the configuration file from - the application's base directory in production,
    /// a temporary directory in tests. The working directory is deliberately not used: the GUI auto-starts
    /// from a registry Run key, whose working directory is not the install folder.
    /// </param>
    /// <returns>The configuration Serilog is read from, with every file-sink path already absolute.</returns>
    public static IConfigurationRoot BuildConfiguration(string basePath)
    {
        var configured = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile(ConfigurationFileName, optional: true, reloadOnChange: false)
            .Build();

        var expandedPaths = ResolveFileSinkPaths(configured);

        return expandedPaths.Count == 0
            ? configured
            : new ConfigurationBuilder()
                .AddConfiguration(configured)
                .AddInMemoryCollection(expandedPaths)
                .Build();
    }

    /// <summary>
    /// Finds every configured sink that names an <c>Args:path</c> and pairs its configuration key with the
    /// expanded path, ready to be layered over the original configuration.
    /// </summary>
    /// <remarks>
    /// Keys are read back from the discovered sections rather than composed from a hardcoded index, so
    /// reordering the <c>WriteTo</c> array or adding a second file sink needs no change here.
    /// </remarks>
    /// <param name="configuration">The configuration read from <see cref="ConfigurationFileName"/>.</param>
    /// <returns>Configuration overrides, one per configured file sink; empty when none names a path.</returns>
    public static IReadOnlyList<KeyValuePair<string, string?>> ResolveFileSinkPaths(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var overrides = new List<KeyValuePair<string, string?>>();

        foreach (var sink in configuration.GetSection(WriteToSection).GetChildren())
        {
            var path = sink.GetSection("Args:path");
            if (!string.IsNullOrWhiteSpace(path.Value))
            {
                overrides.Add(new KeyValuePair<string, string?>(path.Path, ExpandLogPath(path.Value)));
            }
        }

        return overrides;
    }

    /// <summary>
    /// Turns a configured log path into an absolute one: environment variables are expanded, and a blank
    /// value - or one still holding an unresolvable <c>%VARIABLE%</c> - falls back to
    /// <see cref="FallbackLogPath"/>.
    /// </summary>
    /// <remarks>
    /// The unresolvable case matters more than it looks: <see cref="Environment.ExpandEnvironmentVariables"/>
    /// leaves an unknown variable in place verbatim, which would otherwise become a literal
    /// <c>%LOCALAPPDATA%</c> directory created relative to the working directory - a log nobody would find.
    /// </remarks>
    /// <param name="configuredPath">The path as it appears in configuration, or <see langword="null"/>.</param>
    /// <returns>The absolute path the file sink should write to.</returns>
    public static string ExpandLogPath(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return FallbackLogPath();
        }

        var expanded = Environment.ExpandEnvironmentVariables(configuredPath);

        return expanded.Contains('%', StringComparison.Ordinal)
            ? FallbackLogPath()
            : Path.GetFullPath(expanded);
    }

    /// <summary>
    /// Builds a logger from <paramref name="configuration"/>, falling back to a built-in rolling file sink
    /// at <paramref name="fallbackPath"/> when that configuration declares no sink or cannot be applied.
    /// Either way the reason is recorded in the log that does get created.
    /// </summary>
    /// <param name="configuration">The configuration to read Serilog's setup from.</param>
    /// <param name="fallbackPath">The absolute path the built-in sink writes to if the configuration is unusable.</param>
    /// <returns>A logger with at least one sink attached.</returns>
    public static Logger CreateLogger(IConfiguration configuration, string fallbackPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.GetSection(WriteToSection).GetChildren().Any())
        {
            var withoutConfiguration = CreateFallbackLogger(fallbackPath);
            withoutConfiguration.Warning(
                "No Serilog sinks are configured in {ConfigurationFile}; logging to the built-in file sink at {LogPath} instead.",
                ConfigurationFileName,
                fallbackPath);
            return withoutConfiguration;
        }

        try
        {
            return new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .CreateLogger();
        }
#pragma warning disable CA1031 // Do not catch general exception types
        // Bootstrap logging must never be the thing that stops the app from starting, and Serilog's
        // configuration reader throws a different type for every way a section can be wrong (an unknown
        // level name, an unresolvable sink argument, a sink that cannot open its file). Catching the exact
        // set would mean guessing at that list, and guessing wrong reproduces the silent-startup failure
        // this whole type exists to prevent - so every failure is reported into the fallback log instead.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var withBrokenConfiguration = CreateFallbackLogger(fallbackPath);
            withBrokenConfiguration.Error(
                ex,
                "Serilog configuration in {ConfigurationFile} could not be applied; logging to the built-in file sink at {LogPath} instead.",
                ConfigurationFileName,
                fallbackPath);
            return withBrokenConfiguration;
        }
    }

    /// <summary>
    /// Creates the built-in rolling file logger used whenever configuration cannot supply one. Kept at
    /// <c>Debug</c> deliberately: this logger only ever runs when something about the install is already
    /// wrong, which is exactly when the extra detail is worth its size.
    /// </summary>
    /// <param name="path">The absolute rolling log file path to write to.</param>
    /// <returns>A logger writing to <paramref name="path"/>.</returns>
    private static Logger CreateFallbackLogger(string path) =>
        new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.File(
                path,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: FallbackOutputTemplate)
            .CreateLogger();
}
