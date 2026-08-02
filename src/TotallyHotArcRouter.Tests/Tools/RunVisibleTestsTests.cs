using TotallyHot.ArcRouter.Tools;

namespace TotallyHot.ArcRouter.Tests.Tools;

/// <summary>
/// Covers visible-test runner behavior.
/// </summary>
public class RunVisibleTestsTests
{
    [Fact(Skip = "Integration testing disabled")]
    public async Task RunAsync_WithValidTestProject_RunsTests()
    {
        var runVisibleTests = new RunVisibleTests();
        var projectPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", ".."));

        var output = await runVisibleTests.RunAsync(projectPath);

        Assert.DoesNotContain("Error running tests:", output, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(output));
    }

    [Fact(Skip = "Integration testing disabled")]
    public async Task RunAsync_WithInvalidWorkingDirectory_Throws()
    {
        var runVisibleTests = new RunVisibleTests();
        var invalidDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAnyAsync<Exception>(() => runVisibleTests.RunAsync(invalidDirectory));
    }
}

