using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>
/// Covers <see cref="BudgetWindow"/>'s period-key formatting, the lexicographic-ordering property the
/// implementation plan requires (§5.10), and <see cref="BudgetWindowCodec"/>'s round-trip.
/// </summary>
public class BudgetWindowTests
{
    [Fact]
    public void Monthly_PeriodKey_FormatsAsYearDashMonth()
    {
        var window = new BudgetWindow.Monthly();
        Assert.Equal(expected: "2026-08",
            actual: window.PeriodKey(new DateTimeOffset(2026, 8, 7, 12, 0, 0, offset: TimeSpan.Zero)));
    }

    [Fact]
    public void Monthly_NextResetUtc_IsFirstOfNextMonth()
    {
        var window = new BudgetWindow.Monthly();
        var next = window.NextResetUtc(new DateTimeOffset(2026, 8, 7, 12, 0, 0, offset: TimeSpan.Zero));
        Assert.Equal(expected: new DateTimeOffset(2026, 9, 1, 0, 0, 0, offset: TimeSpan.Zero), actual: next);
    }

    [Fact]
    public void Weekly_PeriodKey_FormatsAsIsoWeek()
    {
        var window = new BudgetWindow.Weekly();
        // 2026-08-07 is a Friday in ISO week 32 of 2026.
        Assert.Equal(expected: "2026-W32",
            actual: window.PeriodKey(new DateTimeOffset(2026, 8, 7, 12, 0, 0, offset: TimeSpan.Zero)));
    }

    [Fact]
    public void Weekly_NextResetUtc_IsFollowingMonday()
    {
        var window = new BudgetWindow.Weekly();
        var next = window.NextResetUtc(new DateTimeOffset(2026, 8, 7, 12, 0, 0, offset: TimeSpan.Zero));
        Assert.Equal(expected: DayOfWeek.Monday, actual: next.DayOfWeek);
        Assert.True(next > new DateTimeOffset(2026, 8, 7, 12, 0, 0, offset: TimeSpan.Zero));
    }

    [Fact]
    public void RollingHours_PeriodKey_AdvancesEveryBlock()
    {
        var window = new BudgetWindow.RollingHours(5);
        var start = DateTimeOffset.UnixEpoch.AddHours(100 * 5);

        var withinBlock = window.PeriodKey(start.AddHours(2));
        var nextBlock = window.PeriodKey(start.AddHours(5));

        Assert.Equal(expected: window.PeriodKey(start), actual: withinBlock);
        Assert.NotEqual(expected: withinBlock, actual: nextBlock);
    }

    [Fact]
    public void RollingHours_NonPositiveHours_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BudgetWindow.RollingHours(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BudgetWindow.RollingHours(-1));
    }

    [Theory]
    [MemberData(nameof(WindowKinds))]
    public void PeriodKey_IsLexicographicallyOrderedWithTime(BudgetWindow window)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, offset: TimeSpan.Zero);
        var keys = Enumerable.Range(0, 40)
            .Select(days => window.PeriodKey(start.AddDays(days)))
            .ToList();

        // The property that matters: deduplicated keys, taken in chronological order, already come out
        // lexicographically sorted - no date parsing is ever needed to compare freshness or detect rollover.
        var chronological = keys.Distinct().ToList();
        var lexicographic = chronological.OrderBy(keySelector: k => k, comparer: StringComparer.Ordinal).ToList();
        Assert.Equal(expected: lexicographic, actual: chronological);
    }

    public static TheoryData<BudgetWindow> WindowKinds()
    {
        return
        [
            new BudgetWindow.Monthly(),
            new BudgetWindow.Weekly(),
            new BudgetWindow.RollingHours(5)
        ];
    }

    [Theory]
    [InlineData("Monthly", null)]
    [InlineData("Weekly", null)]
    [InlineData("RollingHours", 5)]
    public void Codec_RoundTrips(string kind, int? hours)
    {
        var window = kind switch
        {
            "Monthly" => (BudgetWindow)new BudgetWindow.Monthly(),
            "Weekly" => new BudgetWindow.Weekly(),
            _ => new BudgetWindow.RollingHours(hours!.Value)
        };

        var (encodedKind, encodedHours) = BudgetWindowCodec.Encode(window);
        Assert.Equal(expected: kind, actual: encodedKind);
        Assert.Equal(expected: hours, actual: encodedHours);

        var decoded = BudgetWindowCodec.Decode(kind: encodedKind, hours: encodedHours);
        Assert.Equal(expected: window, actual: decoded);
    }

    [Fact]
    public void Codec_Decode_UnrecognizedKind_DefaultsToMonthly()
    {
        Assert.Equal(expected: new BudgetWindow.Monthly(), actual: BudgetWindowCodec.Decode(kind: "Nonsense", null));
        Assert.Equal(expected: new BudgetWindow.Monthly(), actual: BudgetWindowCodec.Decode(null, null));
        Assert.Equal(expected: new BudgetWindow.Monthly(), actual: BudgetWindowCodec.Decode(kind: "RollingHours", 0));
    }
}