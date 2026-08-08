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
        Assert.Equal("2026-08", window.PeriodKey(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Monthly_NextResetUtc_IsFirstOfNextMonth()
    {
        var window = new BudgetWindow.Monthly();
        var next = window.NextResetUtc(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Weekly_PeriodKey_FormatsAsIsoWeek()
    {
        var window = new BudgetWindow.Weekly();
        // 2026-08-07 is a Friday in ISO week 32 of 2026.
        Assert.Equal("2026-W32", window.PeriodKey(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Weekly_NextResetUtc_IsFollowingMonday()
    {
        var window = new BudgetWindow.Weekly();
        var next = window.NextResetUtc(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);
        Assert.True(next > new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void RollingHours_PeriodKey_AdvancesEveryBlock()
    {
        var window = new BudgetWindow.RollingHours(5);
        var start = DateTimeOffset.UnixEpoch.AddHours(100 * 5);

        var withinBlock = window.PeriodKey(start.AddHours(2));
        var nextBlock = window.PeriodKey(start.AddHours(5));

        Assert.Equal(window.PeriodKey(start), withinBlock);
        Assert.NotEqual(withinBlock, nextBlock);
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
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var keys = Enumerable.Range(0, 40)
            .Select(days => window.PeriodKey(start.AddDays(days)))
            .ToList();

        // The property that matters: deduplicated keys, taken in chronological order, already come out
        // lexicographically sorted - no date parsing is ever needed to compare freshness or detect rollover.
        var chronological = keys.Distinct().ToList();
        var lexicographic = chronological.OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(lexicographic, chronological);
    }

    public static TheoryData<BudgetWindow> WindowKinds() => new()
    {
        new BudgetWindow.Monthly(),
        new BudgetWindow.Weekly(),
        new BudgetWindow.RollingHours(5),
    };

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
            _ => new BudgetWindow.RollingHours(hours!.Value),
        };

        var (encodedKind, encodedHours) = BudgetWindowCodec.Encode(window);
        Assert.Equal(kind, encodedKind);
        Assert.Equal(hours, encodedHours);

        var decoded = BudgetWindowCodec.Decode(encodedKind, encodedHours);
        Assert.Equal(window, decoded);
    }

    [Fact]
    public void Codec_Decode_UnrecognizedKind_DefaultsToMonthly()
    {
        Assert.Equal(new BudgetWindow.Monthly(), BudgetWindowCodec.Decode("Nonsense", null));
        Assert.Equal(new BudgetWindow.Monthly(), BudgetWindowCodec.Decode(null, null));
        Assert.Equal(new BudgetWindow.Monthly(), BudgetWindowCodec.Decode("RollingHours", 0));
    }
}
