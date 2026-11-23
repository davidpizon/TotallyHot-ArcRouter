using System.Globalization;

namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// Turns money read back from a SQLite <c>REAL</c> column into the <see cref="decimal"/> that was written.
/// </summary>
/// <remarks>
/// Up to .NET 10 the explicit <c>(decimal)</c> cast from <see cref="double"/> rounded to 15 significant
/// digits, so <c>0.03m</c> stored as a double came back as <c>0.03m</c>. From .NET 11 the cast is exact and
/// returns the double's binary value (<c>0.0299999999999999988897769754</c>). Rounding to the 15 digits a
/// double is guaranteed to hold restores the old, written value.
/// </remarks>
internal static class StoredMoney
{
    /// <summary>
    /// Converts a stored double to the decimal it most likely came from: its value rounded to 15
    /// significant digits.
    /// </summary>
    /// <param name="value">The value read from a <c>REAL</c> column.</param>
    /// <returns>The decimal, without the double's representation error.</returns>
    /// <exception cref="OverflowException">The value is not finite or is outside the range of <see cref="decimal"/>.</exception>
    public static decimal FromReal(double value)
    {
        if (!double.IsFinite(value))
            throw new OverflowException($"{value} cannot be represented as a decimal.");

        return decimal.Parse(
            s: value.ToString(format: "G15", provider: CultureInfo.InvariantCulture),
            style: NumberStyles.Float,
            provider: CultureInfo.InvariantCulture);
    }
}
