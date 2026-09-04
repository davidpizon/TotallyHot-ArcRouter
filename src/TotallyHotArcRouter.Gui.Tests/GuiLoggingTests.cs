using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="GuiLogging"/>: the log path it resolves from configuration, and its guarantee
/// that a logger with a working sink comes back even when <c>appsettings.json</c> is missing or broken.
/// That guarantee is the whole point - the installed GUI's blank dashboard was undiagnosable precisely
/// because nothing was written anywhere - so the fallback paths are tested at least as hard as the
/// happy one.
/// </summary>
public sealed class GuiLoggingTests : IDisposable
{
    private readonly string _directory = Path.Combine(path1: Path.GetTempPath(),
        path2: "gui-logging-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the per-test temp directory used for configuration files and log output.</summary>
    public GuiLoggingTests()
    {
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Removes the temp directory and everything the tests wrote into it.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(path: _directory, true);
    }

    [Fact]
    public void FallbackLogPath_LivesUnderThePerUserApplicationDataRoot()
    {
        var expected = Path.Combine(
            path1: Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            path2: "TotallyHotArcRouter",
            path3: "logs",
            path4: "arcrouter-gui-.log");

        GuiLogging.FallbackLogPath().Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExpandLogPath_WithNoConfiguredPath_UsesTheFallback(string? blank)
    {
        GuiLogging.ExpandLogPath(blank).Should().Be(GuiLogging.FallbackLogPath());
    }

    [Fact]
    public void ExpandLogPath_ExpandsEnvironmentVariables()
    {
        var expanded = GuiLogging.ExpandLogPath(@"%LOCALAPPDATA%\TotallyHotArcRouter\logs\arcrouter-gui-.log");

        expanded.Should().Be(GuiLogging.FallbackLogPath());
        expanded.Should().NotContain("%");
    }

    [Fact]
    public void ExpandLogPath_WithAnUnresolvableVariable_UsesTheFallback()
    {
        // Environment.ExpandEnvironmentVariables leaves an unknown variable in place verbatim, which
        // would otherwise create a literal "%NOT_A_REAL_VARIABLE%" directory relative to the working
        // directory - a log file nobody would ever find.
        GuiLogging.ExpandLogPath(@"%NOT_A_REAL_VARIABLE_FOR_TESTS%\arcrouter-gui-.log")
            .Should().Be(GuiLogging.FallbackLogPath());
    }

    [Fact]
    public void ExpandLogPath_MakesARelativePathAbsolute()
    {
        Path.IsPathRooted(GuiLogging.ExpandLogPath(@"logs\arcrouter-gui-.log")).Should().BeTrue();
    }

    [Fact]
    public void ResolveFileSinkPaths_KeysEachOverrideByItsOwnConfigurationPath()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:WriteTo:0:Name"] = "Console",
                ["Serilog:WriteTo:1:Name"] = "File",
                ["Serilog:WriteTo:1:Args:path"] = @"%LOCALAPPDATA%\TotallyHotArcRouter\logs\arcrouter-gui-.log"
            })
            .Build();

        var overrides = GuiLogging.ResolveFileSinkPaths(configuration);

        // Keyed off the discovered section, not a hardcoded index: the file sink is second here.
        overrides.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string?>(key: "Serilog:WriteTo:1:Args:path",
                value: GuiLogging.FallbackLogPath()));
    }

    [Fact]
    public void ResolveFileSinkPaths_WithNoPathArgument_ReturnsNothing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Serilog:WriteTo:0:Name"] = "Console" })
            .Build();

        GuiLogging.ResolveFileSinkPaths(configuration).Should().BeEmpty();
    }

    [Fact]
    public void BuildConfiguration_ReplacesTheConfiguredPathWithTheExpandedOne()
    {
        WriteConfiguration(
            @"{ ""Serilog"": { ""WriteTo"": [ { ""Name"": ""File"", ""Args"": { ""path"": ""%LOCALAPPDATA%\\logs\\gui-.log"" } } ] } }");

        var configuration = GuiLogging.BuildConfiguration(_directory);

        configuration["Serilog:WriteTo:0:Args:path"].Should().Be(
            Path.Combine(path1: Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                path2: "logs", path3: "gui-.log"));
    }

    [Fact]
    public void BuildConfiguration_WithNoFile_ReturnsAnEmptyConfiguration()
    {
        // Optional by design: a missing file must reach CreateLogger's fallback rather than throw here.
        GuiLogging.BuildConfiguration(_directory).GetSection("Serilog:WriteTo").GetChildren().Should().BeEmpty();
    }

    [Fact]
    public void CreateLogger_WithAConfiguredSink_WritesWhereConfigurationSaid()
    {
        var logPath = Path.Combine(path1: _directory, path2: "configured-.log");
        WriteConfiguration(
            $@"{{ ""Serilog"": {{ ""MinimumLevel"": ""Information"", ""WriteTo"": [ {{ ""Name"": ""File"", ""Args"": {{ ""path"": ""{logPath.Replace(oldValue: @"\", newValue: @"\\", comparisonType: StringComparison.Ordinal)}"" }} }} ] }} }}");

        using (var logger = GuiLogging.CreateLogger(configuration: GuiLogging.BuildConfiguration(_directory),
                   fallbackPath: UnusedFallbackPath()))
        {
            logger.Information("Configured sink reached.");
        }

        ReadOnlyLogFile(_directory).Should().Contain("Configured sink reached.");
    }

    [Fact]
    public void CreateLogger_WithNoSinkConfigured_FallsBackAndSaysWhy()
    {
        var fallbackPath = Path.Combine(path1: _directory, path2: "fallback-.log");

        // No appsettings.json at all - the case that used to leave a sink-less logger behind, which is
        // indistinguishable from having no logging at the moment you need it.
        using (var logger = GuiLogging.CreateLogger(configuration: GuiLogging.BuildConfiguration(_directory),
                   fallbackPath: fallbackPath))
        {
            logger.Information("Fallback sink reached.");
        }

        var written = ReadOnlyLogFile(_directory);
        written.Should().Contain("Fallback sink reached.");
        written.Should().Contain("No Serilog sinks are configured");
    }

    [Fact]
    public void CreateLogger_WithUnreadableConfiguration_FallsBackAndRecordsTheFailure()
    {
        // A level name Serilog cannot parse: the configuration declares a sink, so this exercises the
        // catch rather than the no-sink branch above. The sink's own path stays inside the test directory
        // so that if this configuration ever stops throwing, the resulting file shows up as a failed
        // single-file assertion below rather than as litter in the working directory.
        var unreachableSinkPath = Path.Combine(path1: _directory, path2: "unreachable-")
            .Replace(oldValue: @"\", newValue: @"\\", comparisonType: StringComparison.Ordinal);
        WriteConfiguration(
            $@"{{ ""Serilog"": {{ ""MinimumLevel"": ""NotALogLevel"", ""WriteTo"": [ {{ ""Name"": ""File"", ""Args"": {{ ""path"": ""{unreachableSinkPath}.log"" }} }} ] }} }}");
        var fallbackPath = Path.Combine(path1: _directory, path2: "fallback-.log");

        using (var logger = GuiLogging.CreateLogger(configuration: GuiLogging.BuildConfiguration(_directory),
                   fallbackPath: fallbackPath))
        {
            logger.Information("Fallback sink reached after a configuration failure.");
        }

        var written = ReadOnlyLogFile(_directory);
        written.Should().Contain("could not be applied");
        written.Should().Contain("Fallback sink reached after a configuration failure.");
    }

    [Fact]
    public void ShippedConfiguration_ResolvesTheLogUnderThePerUserRoot()
    {
        // The real appsettings.json, reached the same way the GUI reaches it - it lands in this test
        // project's output through the project reference. Asserting against the shipped file (rather
        // than a fixture) is what catches a typo in its Serilog section, which would otherwise only
        // show up as the installed GUI quietly logging to the fallback path.
        var configuration = GuiLogging.BuildConfiguration(AppContext.BaseDirectory);

        configuration["Serilog:WriteTo:0:Name"].Should().Be("File");
        configuration["Serilog:WriteTo:0:Args:path"].Should().Be(GuiLogging.FallbackLogPath());
        configuration["Serilog:MinimumLevel:Override:Microsoft.AspNetCore.Components.WebView"].Should().Be("Debug");
    }

    [Fact]
    public void ShippedConfiguration_ProducesALoggerThatWrites()
    {
        // Same shipped configuration, with only the sink's destination redirected into the test's temp
        // directory so this exercises Serilog actually building the configured sink - a wrong argument
        // name or level would send it down the fallback branch instead - without writing into the real
        // per-user log folder.
        var logPath = Path.Combine(path1: _directory, path2: "shipped-.log");
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(GuiLogging.BuildConfiguration(AppContext.BaseDirectory))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Serilog:WriteTo:0:Args:path"] = logPath })
            .Build();

        using (var logger = GuiLogging.CreateLogger(configuration: configuration, fallbackPath: UnusedFallbackPath()))
        {
            logger.Information("Shipped configuration reached.");
        }

        var written = ReadOnlyLogFile(_directory);
        written.Should().Contain("Shipped configuration reached.");
        written.Should().NotContain("could not be applied");
    }

    /// <summary>Writes <paramref name="json"/> as the test directory's <c>appsettings.json</c>.</summary>
    /// <param name="json">The configuration file contents.</param>
    private void WriteConfiguration(string json)
    {
        File.WriteAllText(path: Path.Combine(path1: _directory, path2: GuiLogging.ConfigurationFileName),
            contents: json);
    }

    /// <summary>
    /// Reads the single rolling log file the test produced. Serilog appends a date to the configured
    /// path, so the file is found by globbing rather than by name.
    /// </summary>
    /// <param name="directory">The directory the log was written to.</param>
    /// <returns>The log file's contents.</returns>
    private static string ReadOnlyLogFile(string directory)
    {
        var file = Directory.GetFiles(path: directory, searchPattern: "*.log").Should().ContainSingle().Subject;

        // Serilog holds the file open with FileShare.Read until the logger is disposed, and the callers
        // above have disposed theirs - but open it shared anyway so a lingering handle cannot fail the
        // assertion for the wrong reason.
        using var stream = new FileStream(path: file, mode: FileMode.Open, access: FileAccess.Read,
            share: FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// A fallback path for the cases that must never use it. Points at the test directory so an
    /// unexpected fallback shows up as an extra file rather than polluting the real per-user log folder.
    /// </summary>
    /// <returns>An absolute path inside the per-test temp directory.</returns>
    private string UnusedFallbackPath()
    {
        return Path.Combine(path1: _directory, path2: "unexpected-fallback-.log");
    }
}