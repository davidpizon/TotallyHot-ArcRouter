using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Sandbox.Scoring;

/// <summary>
/// Maps a populated <see cref="SandboxResult"/>'s structural and execution signals onto the unified score
/// u_i in [0,1], using per-dimension weights (see architecture doc §7). Weights need not pre-sum to 1;
/// the scorer normalizes by their total.
/// </summary>
public sealed class VerifierScorer : IVerifierScorer
{
    private readonly SandboxOptions _options;

    /// <summary>Initializes a new instance of the <see cref="VerifierScorer"/> class.</summary>
    /// <param name="options">The sandbox options carrying per-dimension weights.</param>
    public VerifierScorer(IOptions<SandboxOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <inheritdoc />
    public double Score(SandboxResult result, string dimension)
    {
        ArgumentNullException.ThrowIfNull(result);

        var weights = _options.ResolveWeights(dimension);
        double sSyntax = result.SyntaxValid ? 1.0 : 0.0;
        double sExec = ScoreExecution(result);

        double wSyntax = weights.Syntax;
        double wExec = weights.Exec;

        // When nothing executed (Tier 0, non-executable content, or a degraded host), fold the exec weight
        // into syntax so the score still spans [0,1] rather than being capped by an unreachable component.
        if (!result.Executed)
        {
            wSyntax += wExec;
            wExec = 0.0;
        }

        double total = wSyntax + wExec;
        if (total <= 0.0)
        {
            return 0.0;
        }

        double u = ((wSyntax * sSyntax) + (wExec * sExec)) / total;
        return Math.Clamp(u, 0.0, 1.0);
    }

    /// <summary>Scores the execution component: zero for a non-executed, timed-out, OOM-killed, or seccomp-denied result; full credit for a clean exit, half credit if stderr also has output, and zero for any other non-zero exit.</summary>
    private static double ScoreExecution(SandboxResult result)
    {
        if (!result.Executed || result.TimedOut || result.OomKilled || result.SeccompDenied)
        {
            return 0.0;
        }

        if (result.ExitCode == 0)
        {
            // Clean exit earns full credit; a clean exit with stderr noise earns partial credit.
            return string.IsNullOrEmpty(result.StderrTruncated) ? 1.0 : 0.5;
        }

        return 0.0;
    }
}

