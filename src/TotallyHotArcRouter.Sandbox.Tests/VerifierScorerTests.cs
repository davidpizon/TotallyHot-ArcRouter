using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Sandbox.Scoring;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>Covers the unified-score mapping and per-dimension weighting.</summary>
public class VerifierScorerTests
{
    private static VerifierScorer CreateScorer(SandboxOptions? options = null) =>
        new(Options.Create(options ?? new SandboxOptions()));

    [Fact]
    public void Score_AllWeightsZero_ReturnsZeroRatherThanDividingByZero()
    {
        var options = new SandboxOptions
        {
            DimensionWeights =
            {
                ["zero_weight"] = new DimensionWeightOptions { Syntax = 0.0, Exec = 0.0 },
            },
        };
        var scorer = CreateScorer(options);
        var result = new SandboxResult { SyntaxValid = true, Executed = true, ExitCode = 0 };

        Assert.Equal(0.0, scorer.Score(result, "zero_weight"));
    }

    [Fact]
    public void Score_SyntaxValidNotExecuted_FoldsExecWeightIntoSyntax()
    {
        var options = new SandboxOptions
        {
            DimensionWeights =
            {
                ["code_generation"] = new DimensionWeightOptions { Syntax = 0.2, Exec = 0.8 },
            },
        };
        var scorer = CreateScorer(options);
        var result = new SandboxResult { SyntaxValid = true, Executed = false };

        // Exec weight folds into syntax when nothing ran, so a syntax-valid result scores 1.0.
        Assert.Equal(1.0, scorer.Score(result, "code_generation"));
    }

    [Fact]
    public void Score_ExecutedCleanExit_IsOne()
    {
        var scorer = CreateScorer();
        var result = new SandboxResult { SyntaxValid = true, Executed = true, ExitCode = 0 };

        Assert.Equal(1.0, scorer.Score(result, "code_generation"));
    }

    [Fact]
    public void Score_ExecutedCleanExitWithStderr_IsPartial()
    {
        var options = new SandboxOptions
        {
            DimensionWeights =
            {
                ["code_generation"] = new DimensionWeightOptions { Syntax = 0.0, Exec = 1.0 },
            },
        };
        var scorer = CreateScorer(options);
        var result = new SandboxResult
        {
            SyntaxValid = true,
            Executed = true,
            ExitCode = 0,
            StderrTruncated = "DeprecationWarning: ...",
        };

        Assert.Equal(0.5, scorer.Score(result, "code_generation"));
    }

    [Theory]
    [InlineData(1, false, false, false)]
    [InlineData(0, true, false, false)]
    [InlineData(0, false, true, false)]
    [InlineData(0, false, false, true)]
    public void Score_FailureModes_ZeroExecComponent(int exitCode, bool timedOut, bool oom, bool seccomp)
    {
        var options = new SandboxOptions
        {
            DimensionWeights =
            {
                ["code_generation"] = new DimensionWeightOptions { Syntax = 0.0, Exec = 1.0 },
            },
        };
        var scorer = CreateScorer(options);
        var result = new SandboxResult
        {
            SyntaxValid = true,
            Executed = true,
            ExitCode = exitCode,
            TimedOut = timedOut,
            OomKilled = oom,
            SeccompDenied = seccomp,
        };

        Assert.Equal(0.0, scorer.Score(result, "code_generation"));
    }

    [Fact]
    public void Score_UnknownDimension_UsesBalancedDefault()
    {
        var scorer = CreateScorer();
        var result = new SandboxResult { SyntaxValid = true, Executed = true, ExitCode = 0 };

        Assert.Equal(1.0, scorer.Score(result, "totally_unknown_dimension"));
    }

    [Fact]
    public void Score_AlwaysWithinUnitInterval()
    {
        var scorer = CreateScorer();
        var result = new SandboxResult { SyntaxValid = false, Executed = true, ExitCode = 7 };

        var u = scorer.Score(result, "code_generation");

        Assert.InRange(u, 0.0, 1.0);
    }
}

