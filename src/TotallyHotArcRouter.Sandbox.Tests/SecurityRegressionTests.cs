using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Sandbox.Scoring;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>
/// Asserts that contained hostile outcomes (timeout, OOM, seccomp denial, non-zero exit) map to the lowest
/// reward for execution-scored dimensions. The actual containment (air-gap, cgroups, seccomp) is exercised
/// by Linux+privilege integration tests; here we lock the reward-mapping half of the contract cross-platform.
/// </summary>
public class SecurityRegressionTests
{
    private static VerifierScorer ExecWeightedScorer()
    {
        var options = new SandboxOptions
        {
            DimensionWeights = { ["code_generation"] = new DimensionWeightOptions { Syntax = 0.0, Exec = 1.0 } },
        };
        return new VerifierScorer(Options.Create(options));
    }

    public static TheoryData<SandboxResult> HostileOutcomes()
    {
        var data = new TheoryData<SandboxResult>();
        data.Add(new SandboxResult { SyntaxValid = true, Executed = true, TimedOut = true });      // infinite loop
        data.Add(new SandboxResult { SyntaxValid = true, Executed = true, OomKilled = true });     // memory bomb
        data.Add(new SandboxResult { SyntaxValid = true, Executed = true, SeccompDenied = true }); // syscall escape attempt
        data.Add(new SandboxResult { SyntaxValid = true, Executed = true, ExitCode = 1 });         // crash / error exit
        data.Add(new SandboxResult { SyntaxValid = true, Executed = true, ExitCode = 137 });       // SIGKILL
        return data;
    }

    [Theory]
    [MemberData(nameof(HostileOutcomes))]
    public void HostileOutcome_ScoresZeroForExecutableDimension(SandboxResult result)
    {
        var scorer = ExecWeightedScorer();

        Assert.Equal(0.0, scorer.Score(result, "code_generation"));
    }

    [Fact]
    public void CleanRun_ScoresOne()
    {
        var scorer = ExecWeightedScorer();
        var result = new SandboxResult { SyntaxValid = true, Executed = true, ExitCode = 0 };

        Assert.Equal(1.0, scorer.Score(result, "code_generation"));
    }
}

