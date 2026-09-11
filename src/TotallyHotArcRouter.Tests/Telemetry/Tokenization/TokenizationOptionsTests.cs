using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Telemetry.Tokenization;

namespace TotallyHot.ArcRouter.Tests.Telemetry.Tokenization;

/// <summary>
/// Covers <see cref="TokenizationOptions"/>: the defaults - above all that calibration is off out of the
/// box, which is the security posture ADR-0009 rests on - and the eager validation that rejects a
/// nonsensical configuration at startup rather than mid-cycle.
/// </summary>
public class TokenizationOptionsTests
{
    [Fact]
    public void CalibrationEnabled_DefaultsToFalse()
    {
        // ADR-0009's central safety claim, asserted at its source. A second outbound egress destination in
        // a process that proxies other people's prompts must not switch itself on.
        Assert.False(new TokenizationOptions().CalibrationEnabled);
    }

    [Fact]
    public void ApiKeyEnvVar_DefaultsToEmpty_SoEnablingAloneStillGrantsNoCredential()
    {
        Assert.Equal(expected: string.Empty, actual: new TokenizationOptions().ApiKeyEnvVar);
    }

    [Fact]
    public void Defaults_AreTheDocumentedValues()
    {
        var options = new TokenizationOptions();

        Assert.Equal(expected: 60, actual: options.CycleIntervalMinutes);
        Assert.Equal(expected: 20, actual: options.MaxSamplesPerCycle);
        Assert.Equal(expected: 10, actual: options.MinSamplesForTrust);
        Assert.Equal(expected: 0.2d, actual: options.SmoothingFactor, tolerance: 0.0001d);
    }

    [Fact]
    public void EnsureValid_Defaults_DoNotThrow()
    {
        Assert.Null(Record.Exception(() => new TokenizationOptions().EnsureValid()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EnsureValid_NonPositiveCycleInterval_Throws(int minutes)
    {
        var options = new TokenizationOptions { CycleIntervalMinutes = minutes };

        var exception = Assert.Throws<OptionsValidationException>(options.EnsureValid);

        Assert.Contains(collection: exception.Failures, filter: f => f.Contains(
            value: "CycleIntervalMinutes", comparisonType: StringComparison.Ordinal));
    }

    [Fact]
    public void EnsureValid_NonPositiveSampleBound_Throws()
    {
        var options = new TokenizationOptions { MaxSamplesPerCycle = 0 };

        var exception = Assert.Throws<OptionsValidationException>(options.EnsureValid);

        Assert.Contains(collection: exception.Failures, filter: f => f.Contains(
            value: "MaxSamplesPerCycle", comparisonType: StringComparison.Ordinal));
    }

    [Fact]
    public void EnsureValid_NonPositiveTrustThreshold_Throws()
    {
        var options = new TokenizationOptions { MinSamplesForTrust = 0 };

        var exception = Assert.Throws<OptionsValidationException>(options.EnsureValid);

        Assert.Contains(collection: exception.Failures, filter: f => f.Contains(
            value: "MinSamplesForTrust", comparisonType: StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-0.5d)]
    [InlineData(1.5d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void EnsureValid_SmoothingFactorOutsideItsRange_Throws(double smoothing)
    {
        // A factor outside (0, 1] would either freeze the running mean or overshoot every sample, silently
        // corrupting every calibrated count rather than failing loudly.
        var options = new TokenizationOptions { SmoothingFactor = smoothing };

        var exception = Assert.Throws<OptionsValidationException>(options.EnsureValid);

        Assert.Contains(collection: exception.Failures, filter: f => f.Contains(
            value: "SmoothingFactor", comparisonType: StringComparison.Ordinal));
    }

    [Fact]
    public void EnsureValid_SeveralBadValues_ReportsThemAllAtOnce()
    {
        var options = new TokenizationOptions
        {
            CycleIntervalMinutes = 0,
            MaxSamplesPerCycle = 0,
            SmoothingFactor = 3d
        };

        var exception = Assert.Throws<OptionsValidationException>(options.EnsureValid);

        Assert.Equal(expected: 3, actual: exception.Failures.Count());
    }

    [Fact]
    public void SectionName_BindsUnderTheCostTrackingTree()
    {
        Assert.Equal(expected: "CostTracking:Tokenization", actual: TokenizationOptions.SectionName);
    }
}
