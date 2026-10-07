using Serilog;
using Serilog.Core;
using Serilog.Events;
using TotallyHot.ArcRouter.Logging;

namespace TotallyHot.ArcRouter.Tests.Logging;

/// <summary>Covers the one-time pre-upgrade F9 log rewrite (#184 phase 3).</summary>
public sealed class PreUpgradeLogRewriteTests : IDisposable
{
    private static readonly ILogger Silent = new LoggerConfiguration().CreateLogger();

    private readonly string _root = Path.Combine(TestScratchDirectory.RunRoot,
        "rewrite-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _logs;

    /// <summary>Creates an isolated data root and logs directory for each test.</summary>
    public PreUpgradeLogRewriteTests()
    {
        _logs = Path.Combine(_root, "logs");
        Directory.CreateDirectory(_logs);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>Planted F9 lines are removed; every other line is kept.</summary>
    [Fact]
    public void Run_RemovesF9LinesAndKeepsOthers()
    {
        var path = Path.Combine(_logs, "arcrouter-20261006.log");
        File.WriteAllText(path, """
            keep me
            2026-10-06 [DBG] [INTERCEPTOR] Intercepted agent request message: secret prompt
            2026-10-06 [DBG] [INTERCEPTOR] Newest user message: hello
            also keep me
            2026-10-06 [DBG] [INTERCEPTOR] Intercepted agent response message: secret reply
            2026-10-06 [DBG] [INTERCEPTOR] Assembled LLM response text: assembled
            trailing
            """);

        PreUpgradeLogRewrite.Run(_root, _logs, Silent);

        var rewritten = File.ReadAllText(path);
        Assert.Contains("keep me", rewritten, StringComparison.Ordinal);
        Assert.Contains("also keep me", rewritten, StringComparison.Ordinal);
        Assert.Contains("trailing", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("Intercepted agent request message", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("Newest user message", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("Assembled LLM response text", rewritten, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, PreUpgradeLogRewrite.MarkerFileName)));
    }

    /// <summary>A second run is a no-op once the marker exists.</summary>
    [Fact]
    public void Run_WithMarkerPresent_DoesNotTouchLogsAgain()
    {
        var path = Path.Combine(_logs, "arcrouter-20261006.log");
        File.WriteAllText(path, "original\n");
        File.WriteAllText(Path.Combine(_root, PreUpgradeLogRewrite.MarkerFileName), string.Empty);

        PreUpgradeLogRewrite.Run(_root, _logs, Silent);

        Assert.Equal("original\n", File.ReadAllText(path));
    }

    /// <summary>
    /// A file that can be neither rewritten nor deleted leaves the marker unwritten, so the next run retries
    /// it. Windows only: a share-none handle blocks both operations there, while Unix lets the delete through.
    /// </summary>
    [Fact]
    public void Run_WhenAFileCannotBeCleaned_DoesNotWriteTheMarker()
    {
        Assert.SkipUnless(condition: OperatingSystem.IsWindows(), reason: "Needs Windows share-mode locking.");

        var path = Path.Combine(_logs, "arcrouter-20261006.log");
        File.WriteAllText(path, "[INTERCEPTOR] Newest user message: planted\n");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            PreUpgradeLogRewrite.Run(_root, _logs, Silent);

        Assert.False(File.Exists(Path.Combine(_root, PreUpgradeLogRewrite.MarkerFileName)));

        PreUpgradeLogRewrite.Run(_root, _logs, Silent);

        Assert.True(File.Exists(Path.Combine(_root, PreUpgradeLogRewrite.MarkerFileName)));
        Assert.DoesNotContain("planted", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>Every listed logs directory is cleaned and one marker covers all of them.</summary>
    [Fact]
    public void Run_WithSeveralDirectories_CleansEachAndWritesOneMarker()
    {
        var other = Path.Combine(_root, "other-logs");
        Directory.CreateDirectory(other);
        var first = Path.Combine(_logs, "arcrouter-20261006.log");
        var second = Path.Combine(other, "arcrouter-20261006.log");
        File.WriteAllText(first, "[INTERCEPTOR] Newest user message: one\nkeep\n");
        File.WriteAllText(second, "[INTERCEPTOR] Newest user message: two\nkeep\n");

        PreUpgradeLogRewrite.Run(_root, [_logs, other, Path.Combine(_root, "missing")], Silent);

        Assert.DoesNotContain("Newest user message", File.ReadAllText(first), StringComparison.Ordinal);
        Assert.DoesNotContain("Newest user message", File.ReadAllText(second), StringComparison.Ordinal);
        Assert.Contains("keep", File.ReadAllText(second), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, PreUpgradeLogRewrite.MarkerFileName)));
    }

    /// <summary>
    /// A data root that cannot hold the marker logs one readable error without an exception attached and does
    /// not throw, so a read-only root never stops startup.
    /// </summary>
    [Fact]
    public void Run_WhenTheMarkerCannotBeWritten_LogsAReadableErrorAndDoesNotThrow()
    {
        var blocker = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(blocker, string.Empty);
        var events = new List<LogEvent>();
        using var logger = new LoggerConfiguration().WriteTo.Sink(new CollectingSink(events)).CreateLogger();

        PreUpgradeLogRewrite.Run(Path.Combine(blocker, "data"), _logs, logger);

        var error = Assert.Single(events, e => e.Level == LogEventLevel.Error);
        Assert.Null(error.Exception);
        Assert.Contains("Could not record", error.RenderMessage(), StringComparison.Ordinal);
    }

    private sealed class CollectingSink(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Add(logEvent);
    }

    /// <summary>macOS launchd console copies get the same treatment.</summary>
    [Fact]
    public void Run_RewritesLaunchdStdout()
    {
        var path = Path.Combine(_logs, "launchd-stdout.log");
        File.WriteAllText(path, """
            ok
            [INTERCEPTOR] Newest user message: planted
            """);

        PreUpgradeLogRewrite.Run(_root, _logs, Silent);

        var rewritten = File.ReadAllText(path);
        Assert.Contains("ok", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("Newest user message", rewritten, StringComparison.Ordinal);
    }
}
