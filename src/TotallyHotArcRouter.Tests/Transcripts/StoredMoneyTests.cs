using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Transcripts;

/// <summary>
/// Proves <see cref="StoredMoney.FromReal"/> gives back the decimal that was stored as a SQLite REAL,
/// without the double's representation error that the .NET 11 <c>(decimal)</c> cast keeps.
/// </summary>
public sealed class StoredMoneyTests
{
    /// <summary>Values a double cannot hold exactly come back as the decimal that was written.</summary>
    [Theory]
    [InlineData("0.03")]
    [InlineData("0.055")]
    [InlineData("15.005")]
    [InlineData("-0.04")]
    [InlineData("0.000001")]
    [InlineData("0")]
    [InlineData("123456.789")]
    public void FromReal_RoundTripsTheWrittenDecimal(string written)
    {
        var expected = decimal.Parse(s: written, provider: System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected: expected, actual: StoredMoney.FromReal((double)expected));
    }

    /// <summary>Very small values, which the round-trip format writes with an exponent, still parse.</summary>
    [Fact]
    public void FromReal_ParsesExponentForm()
    {
        Assert.Equal(expected: 0.0000000001m, actual: StoredMoney.FromReal(1e-10));
    }

    /// <summary>NaN and infinities cannot be money, so they fail the way the old cast did.</summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void FromReal_RejectsNonFiniteValues(double value)
    {
        Assert.Throws<OverflowException>(() => StoredMoney.FromReal(value));
    }
}
