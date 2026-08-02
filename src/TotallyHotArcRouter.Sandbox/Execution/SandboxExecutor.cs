using System.Diagnostics;
using TotallyHot.ArcRouter.Sandbox.Redaction;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Sandbox.Execution;

/// <summary>
/// Orchestrates a single evaluation: always produces a Tier-0 structural verdict, runs the snippet in the
/// selected tier's runtime when one is available, applies seccomp-denial escalation, and scores the
/// result. Falls back cleanly to a Tier-0-only result when execution is unavailable.
/// </summary>
public sealed class SandboxExecutor : ISandboxExecutor
{
    private readonly ISandboxCapabilityProbe _probe;
    private readonly ITierSelector _tierSelector;
    private readonly IStructuralParser _structuralParser;
    private readonly IVerifierScorer _scorer;
    private readonly IReadOnlyDictionary<SandboxTier, ISandboxRuntime> _runtimes;
    private readonly SandboxOptions _options;
    private readonly ILogger<SandboxExecutor> _logger;

    /// <summary>Initializes a new instance of the <see cref="SandboxExecutor"/> class.</summary>
    /// <param name="probe">The capability probe.</param>
    /// <param name="tierSelector">The tier selector.</param>
    /// <param name="structuralParser">The Tier-0 structural parser.</param>
    /// <param name="scorer">The verifier scorer.</param>
    /// <param name="runtimes">The registered per-tier runtimes (may be empty for Tier-0-only hosts).</param>
    /// <param name="options">The sandbox options.</param>
    /// <param name="logger">The logger.</param>
    public SandboxExecutor(
        ISandboxCapabilityProbe probe,
        ITierSelector tierSelector,
        IStructuralParser structuralParser,
        IVerifierScorer scorer,
        IEnumerable<ISandboxRuntime> runtimes,
        IOptions<SandboxOptions> options,
        ILogger<SandboxExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(tierSelector);
        ArgumentNullException.ThrowIfNull(structuralParser);
        ArgumentNullException.ThrowIfNull(scorer);
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _probe = probe;
        _tierSelector = tierSelector;
        _structuralParser = structuralParser;
        _scorer = scorer;
        _runtimes = BuildRuntimeMap(runtimes);
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SandboxResult> ExecuteAsync(SandboxRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        var syntax = _structuralParser.Check(request.Code, request.Language);

        var selectedTier = _tierSelector.Select(request);
        SandboxResult result;

        if (selectedTier == SandboxTier.Tier0Static || !_runtimes.TryGetValue(selectedTier, out var runtime))
        {
            result = BuildStaticResult(request, syntax, stopwatch.ElapsedMilliseconds, DegradedReasonFor(selectedTier));
        }
        else
        {
            try
            {
                var outcome = await runtime.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
                var tier = runtime.Tier;

                if (outcome.SeccompDenied
                    && _options.EscalateOnSeccompDenial
                    && tier == SandboxTier.Tier1Jail
                    && _runtimes.TryGetValue(SandboxTier.Tier2MicroVm, out var escalated))
                {
                    _logger.LogInformation(
                        "Tier 1 seccomp denial for {Language} (correlation {CorrelationId}); escalating to Tier 2.",
                        request.Language,
                        request.CorrelationId);
                    outcome = await escalated.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
                    tier = escalated.Tier;
                }

                result = BuildExecutedResult(request, syntax, tier, outcome, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A runtime failure (missing binary, snapshot misconfig, unexpected I/O) must not lose the
                // request: fall back to the Tier-0 structural signal we already have.
                _logger.LogWarning(
                    ex,
                    "Tier {Tier} execution failed for {Language} (correlation {CorrelationId}); falling back to Tier 0.",
                    selectedTier,
                    request.Language,
                    request.CorrelationId);
                result = BuildStaticResult(request, syntax, stopwatch.ElapsedMilliseconds, "tier-execution-failed");
            }
        }

        var score = _scorer.Score(result, request.Dimension);
        result = result with { UnifiedScore = score };

        _logger.LogInformation(
            "Sandbox evaluated {Language} dim {Dimension} tier {Tier} executed={Executed} -> u={Score:F3} (correlation {CorrelationId}).",
            request.Language,
            request.Dimension,
            result.Tier,
            result.Executed,
            score,
            request.CorrelationId);

        return result;
    }

    /// <summary>Builds a lookup of the registered runtimes keyed by tier, with the last registration for a tier winning.</summary>
    private static Dictionary<SandboxTier, ISandboxRuntime> BuildRuntimeMap(IEnumerable<ISandboxRuntime> runtimes)
    {
        var map = new Dictionary<SandboxTier, ISandboxRuntime>();
        foreach (var runtime in runtimes)
        {
            // Last registration wins for a given tier, mirroring DI's single-service resolution.
            map[runtime.Tier] = runtime;
        }

        return map;
    }

    /// <summary>Determines why a result should be marked degraded, based on runtime probe availability and whether execution actually ran.</summary>
    private string? DegradedReasonFor(SandboxTier selectedTier)
    {
        if (!_probe.IsExecutionAvailable)
        {
            return _probe.DegradedReason;
        }

        return selectedTier == SandboxTier.Tier0Static ? null : "no-runtime-registered";
    }

    /// <summary>Builds a result for a request that was only syntax-checked and never executed (Tier 0).</summary>
    private SandboxResult BuildStaticResult(SandboxRequest request, SyntaxVerdict syntax, long wallClockMs, string? degradedReason) =>
        new()
        {
            RequestCorrelationId = request.CorrelationId,
            SessionId = request.SessionId,
            Dimension = request.Dimension,
            Model = request.Model,
            Language = request.Language.ToString(),
            Tier = nameof(SandboxTier.Tier0Static),
            SyntaxValid = syntax.IsValid,
            Executed = false,
            WallClockMs = wallClockMs,
            DegradedReason = degradedReason,
        };

    /// <summary>Builds a result for a request that ran in a real runtime, capturing exit status, captured output, and resource usage.</summary>
    private SandboxResult BuildExecutedResult(
        SandboxRequest request,
        SyntaxVerdict syntax,
        SandboxTier tier,
        ExecutionOutcome outcome,
        long wallClockMs) =>
        new()
        {
            RequestCorrelationId = request.CorrelationId,
            SessionId = request.SessionId,
            Dimension = request.Dimension,
            Model = request.Model,
            Language = request.Language.ToString(),
            Tier = tier.ToString(),
            SyntaxValid = syntax.IsValid,
            Executed = true,
            ExitCode = outcome.ExitCode,
            TimedOut = outcome.TimedOut,
            OomKilled = outcome.OomKilled,
            SeccompDenied = outcome.SeccompDenied,
            StdoutTruncated = Cap(outcome.Stdout),
            StderrTruncated = Cap(outcome.Stderr),
            WallClockMs = outcome.WallClockMs > 0 ? outcome.WallClockMs : wallClockMs,
            PeakMemoryBytes = outcome.PeakMemoryBytes,
        };

    /// <summary>Redacts likely secrets from captured output and then truncates it to the configured maximum size.</summary>
    private string Cap(string value)
    {
        // Redact likely secrets first, then size-cap, so capping never leaves half a secret exposed.
        var redacted = OutputRedactor.Redact(value);
        return redacted.Length <= _options.MaxCapturedOutputBytes
            ? redacted
            : redacted[.._options.MaxCapturedOutputBytes];
    }
}

