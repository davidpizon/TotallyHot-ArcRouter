using System.Text.Json.Serialization;

namespace TotallyHot.ArcRouter.Sandbox;

/// <summary>
/// The structured signal record produced by every sandbox evaluation — success, failure, timeout, or
/// extraction-only. Mirrors the JSON contract in the sandboxed-executor architecture doc (§8.3) and is
/// the source of the unified score fed back into router memory.
/// </summary>
public sealed record SandboxResult
{
    /// <summary>The contract version of this record's shape.</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "1.0";

    /// <summary>Correlation id tying this signal back to its routing telemetry event.</summary>
    [JsonPropertyName("requestCorrelationId")]
    public string RequestCorrelationId { get; init; } = string.Empty;

    /// <summary>The routed request's session id.</summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    /// <summary>The inferred task dimension.</summary>
    [JsonPropertyName("dimension")]
    public string Dimension { get; init; } = string.Empty;

    /// <summary>The model that produced the evaluated response, used as the score's model key.</summary>
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    /// <summary>The evaluated snippet's language.</summary>
    [JsonPropertyName("language")]
    public string Language { get; init; } = string.Empty;

    /// <summary>The tier that produced this result.</summary>
    [JsonPropertyName("tier")]
    public string Tier { get; init; } = nameof(SandboxTier.Tier0Static);

    /// <summary>Whether the structural (parse/compile) check passed. Source of s_syntax.</summary>
    [JsonPropertyName("syntaxValid")]
    public bool SyntaxValid { get; init; }

    /// <summary>Whether the snippet was actually executed (false for Tier 0 / non-executable content).</summary>
    [JsonPropertyName("executed")]
    public bool Executed { get; init; }

    /// <summary>The process/VM exit code, or <see langword="null"/> when not executed.</summary>
    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; init; }

    /// <summary>Whether the supervisor killed the run for exceeding the wall-clock ceiling.</summary>
    [JsonPropertyName("timedOut")]
    public bool TimedOut { get; init; }

    /// <summary>Whether the run was OOM-killed by the memory cgroup.</summary>
    [JsonPropertyName("oomKilled")]
    public bool OomKilled { get; init; }

    /// <summary>Whether the run was terminated by a seccomp denial.</summary>
    [JsonPropertyName("seccompDenied")]
    public bool SeccompDenied { get; init; }

    /// <summary>Size-capped, redaction-scanned stdout.</summary>
    [JsonPropertyName("stdoutTruncated")]
    public string StdoutTruncated { get; init; } = string.Empty;

    /// <summary>Size-capped, redaction-scanned stderr.</summary>
    [JsonPropertyName("stderrTruncated")]
    public string StderrTruncated { get; init; } = string.Empty;

    /// <summary>Wall-clock duration of the evaluation in milliseconds.</summary>
    [JsonPropertyName("wallClockMs")]
    public long WallClockMs { get; init; }

    /// <summary>Peak resident memory in bytes, or 0 when not measured.</summary>
    [JsonPropertyName("peakMemoryBytes")]
    public long PeakMemoryBytes { get; init; }

    /// <summary>The unified score u_i in [0,1].</summary>
    [JsonPropertyName("unifiedScore")]
    public double UnifiedScore { get; init; }

    /// <summary>Why execution was skipped/degraded (e.g. <c>host-not-linux</c>), or null when fully evaluated.</summary>
    [JsonPropertyName("degradedReason")]
    public string? DegradedReason { get; init; }
}

