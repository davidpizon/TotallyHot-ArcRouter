using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Tests.Models;

/// <summary>
/// Covers initialization behavior for <see cref="RoutingResult"/>.
/// </summary>
public class RoutingResultTests
{
    /// <summary>
    /// Verifies that initialized values are preserved.
    /// </summary>
    [Fact]
    public void Init_SetsExpectedValues()
    {
        var decision = new RoutingDecision(
            "kimi-k2.5",
            0.9,
            "Selected best model.",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var result = new RoutingResult
        {
            Decision = decision,
            Response = "ok"
        };

        Assert.Same(decision, result.Decision);
        Assert.Equal("ok", result.Response);
    }
}

