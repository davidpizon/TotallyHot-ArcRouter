using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace TotallyHot.ArcRouter.Logging;

/// <summary>
/// Serilog sink that writes only <see cref="ConversationBodyLogging.PropertyName"/>-marked events to
/// rolling <c>bodies-*.log</c> files, and the <see cref="IBodyLogController"/> Clear uses to delete those
/// files under an open sink (#184 phase 3).
/// </summary>
public sealed class BodyLogController : IBodyLogController, ILogEventSink, IDisposable
{
    private const string FileNamePrefix = "bodies-";
    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    private readonly Lock _gate = new();
    private readonly string _logsDirectory;
    private readonly Func<bool> _isEnabled;
    private Logger? _inner;
    private bool _disposed;

    /// <summary>
    /// Creates a controller that writes under <paramref name="logsDirectory"/> when
    /// <paramref name="isEnabled"/> returns true. The enabled check is a delegate so the Serilog
    /// pipeline can be wired before DI has built the body-excerpt options monitor, matching
    /// <c>DeferredTelemetryPublisher</c>.
    /// </summary>
    /// <param name="logsDirectory">Directory that holds <c>bodies-*.log</c> (and <c>arcrouter-*.log</c>).</param>
    /// <param name="isEnabled">Returns whether body-excerpt logging is currently on.</param>
    public BodyLogController(string logsDirectory, Func<bool> isEnabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        ArgumentNullException.ThrowIfNull(isEnabled);

        _logsDirectory = logsDirectory;
        _isEnabled = isEnabled;
    }

    /// <inheritdoc/>
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        // Marker first (defense for direct Emit / tests; Program also ByIncludingOnly-filters). Enabled
        // is an options-monitor lookup. _disposed is only read under the gate so Emit and Dispose agree.
        if (!logEvent.Properties.ContainsKey(ConversationBodyLogging.PropertyName)) return;
        if (!_isEnabled()) return;

        lock (_gate)
        {
            if (_disposed) return;
            EnsureOpenUnlocked();
            _inner!.Write(logEvent);
        }
    }

    /// <inheritdoc/>
    public bool ClearBodyFiles()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            DisposeInnerUnlocked();

            var allDeleted = true;
            if (Directory.Exists(_logsDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(_logsDirectory, FileNamePrefix + "*.log"))
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        allDeleted = false;
                        // Clear still finishes transcript deletion; surface the leftover via the return
                        // value so ClearTranscripts can set deletion_final false for the UI.
                        Log.Warning(ex,
                            "Could not delete body log {BodyLogPath}; remove it manually if it should not remain.",
                            path);
                    }
                }
            }

            if (_isEnabled()) EnsureOpenUnlocked();
            return allDeleted;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeInnerUnlocked();
        }
    }

    private void EnsureOpenUnlocked()
    {
        if (_inner is not null) return;

        Directory.CreateDirectory(_logsDirectory);
        _inner = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.File(
                path: Path.Combine(_logsDirectory, FileNamePrefix + ".log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: OutputTemplate)
            .CreateLogger();
    }

    private void DisposeInnerUnlocked()
    {
        _inner?.Dispose();
        _inner = null;
    }
}
