using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers domain validation for <see cref="CostReconciliationOptions"/>.</summary>
public class CostReconciliationOptionsTests
{
    [Fact]
    public void EnsureValid_DefaultConfiguration_DoesNotThrow()
    {
        new CostReconciliationOptions().EnsureValid();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EnsureValid_Throws_WhenPollIntervalHoursIsNotPositive(int pollIntervalHours)
    {
        var options = new CostReconciliationOptions { PollIntervalHours = pollIntervalHours };

        Assert.Throws<OptionsValidationException>(() => options.EnsureValid());
    }
}
