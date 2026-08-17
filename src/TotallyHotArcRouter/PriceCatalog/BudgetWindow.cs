using System.Globalization;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// The window a per-provider budget cap resets on (Phase 4, §5.10). <see cref="Monthly"/> matches a
/// typical billing cycle and stays the default - nothing changes for an operator who never opts in to a
/// different window. <see cref="Weekly"/> and <see cref="RollingHours"/> exist because subscription-tier
/// rate limits are commonly enforced on those windows (e.g. Anthropic's 5-hour and weekly limits), and a
/// monthly-only cap cannot warn an operator about the limit they are actually about to hit first.
/// </summary>
public abstract record BudgetWindow
{
    /// <summary>
    /// Private - only the nested <see cref="Monthly"/>, <see cref="Weekly"/>, and <see cref="RollingHours"/>
    /// records may derive from this type, closing the hierarchy so a <c>switch</c> over kind can be
    /// exhaustive without a default arm.
    /// </summary>
    private BudgetWindow()
    {
    }

    /// <summary>
    /// Computes the period key <paramref name="instant"/> falls in. Keys are lexicographically ordered
    /// within a single window kind - the same property today's <c>"yyyy-MM"</c> key already has - so
    /// freshness and rollover comparisons never need to parse a date back out of the key.
    /// </summary>
    public abstract string PeriodKey(DateTimeOffset instant);

    /// <summary>
    /// Computes the instant, in UTC, at which the period containing <paramref name="instant"/> ends and
    /// the next one begins. Used to render "resets in 2h 10m" instead of assuming month-end.
    /// </summary>
    public abstract DateTimeOffset NextResetUtc(DateTimeOffset instant);

    /// <summary>Calendar month in UTC, e.g. <c>"2026-08"</c>. The default and today's only behavior.</summary>
    public sealed record Monthly : BudgetWindow
    {
        /// <inheritdoc />
        public override string PeriodKey(DateTimeOffset instant) =>
            instant.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        /// <inheritdoc />
        public override DateTimeOffset NextResetUtc(DateTimeOffset instant)
        {
            var utc = instant.UtcDateTime;
            var startOfMonth = new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            return new DateTimeOffset(startOfMonth.AddMonths(1));
        }
    }

    /// <summary>ISO-8601 week in UTC, e.g. <c>"2026-W32"</c>.</summary>
    public sealed record Weekly : BudgetWindow
    {
        /// <inheritdoc />
        public override string PeriodKey(DateTimeOffset instant)
        {
            var utc = instant.UtcDateTime;
            var isoYear = ISOWeek.GetYear(utc);
            var isoWeek = ISOWeek.GetWeekOfYear(utc);
            return string.Create(CultureInfo.InvariantCulture, $"{isoYear:D4}-W{isoWeek:D2}");
        }

        /// <inheritdoc />
        public override DateTimeOffset NextResetUtc(DateTimeOffset instant)
        {
            var utc = instant.UtcDateTime;
            var isoYear = ISOWeek.GetYear(utc);
            var isoWeek = ISOWeek.GetWeekOfYear(utc);
            var startOfWeek = DateTime.SpecifyKind(ISOWeek.ToDateTime(isoYear, isoWeek, DayOfWeek.Monday), DateTimeKind.Utc);
            return new DateTimeOffset(startOfWeek.AddDays(7));
        }
    }

    /// <summary>
    /// Fixed-length rolling blocks anchored at the Unix epoch, e.g. a 5-hour window keys as
    /// <c>"R5H-0000091234"</c>. The zero-padded index keeps keys lexicographically ordered indefinitely.
    /// </summary>
    /// <param name="Hours">Block length in hours. Must be positive.</param>
    public sealed record RollingHours(int Hours) : BudgetWindow
    {
        /// <summary>Block length in hours. Must be positive.</summary>
        public int Hours { get; } = Hours > 0
            ? Hours
            : throw new ArgumentOutOfRangeException(nameof(Hours), Hours, "Block length must be positive.");

        /// <inheritdoc />
        public override string PeriodKey(DateTimeOffset instant)
        {
            var index = BlockIndex(instant);
            return string.Create(CultureInfo.InvariantCulture, $"R{Hours}H-{index:D10}");
        }

        /// <inheritdoc />
        public override DateTimeOffset NextResetUtc(DateTimeOffset instant)
        {
            var index = BlockIndex(instant);
            return DateTimeOffset.UnixEpoch.AddHours((index + 1) * (double)Hours);
        }

        // Math.Floor, not a (long) cast - a cast truncates toward zero, which mis-buckets instants before
        // UnixEpoch (e.g. -0.1h would truncate to block 0 instead of flooring to block -1, the actual prior
        // block that instant falls in).
        /// <summary>Computes the zero-based index of the rolling block that <paramref name="instant"/> falls in.</summary>
        /// <param name="instant">The instant to bucket.</param>
        /// <returns>The block index, which may be negative for instants before the Unix epoch.</returns>
        private long BlockIndex(DateTimeOffset instant) =>
            (long)Math.Floor((instant - DateTimeOffset.UnixEpoch).TotalHours / Hours);
    }
}

/// <summary>
/// Converts a <see cref="BudgetWindow"/> to and from the <c>window_kind</c>/<c>window_hours</c> columns
/// persisted on <c>provider_budgets</c>. Kept separate from <see cref="BudgetWindow"/> itself so the
/// domain type stays free of storage concerns.
/// </summary>
public static class BudgetWindowCodec
{
    /// <summary>Encodes a <see cref="BudgetWindow"/> into its persisted (kind, hours) representation.</summary>
    public static (string Kind, int? Hours) Encode(BudgetWindow window) => window switch
    {
        BudgetWindow.Monthly => ("Monthly", null),
        BudgetWindow.Weekly => ("Weekly", null),
        BudgetWindow.RollingHours rolling => ("RollingHours", rolling.Hours),
        _ => throw new ArgumentOutOfRangeException(nameof(window), window, "Unknown budget window kind."),
    };

    /// <summary>
    /// Decodes a persisted (kind, hours) pair back into a <see cref="BudgetWindow"/>. An unrecognized or
    /// missing kind decodes as <see cref="BudgetWindow.Monthly"/> - the safe default that matches every
    /// row written before this column existed.
    /// </summary>
    public static BudgetWindow Decode(string? kind, int? hours) => kind switch
    {
        "Weekly" => new BudgetWindow.Weekly(),
        "RollingHours" when hours is > 0 => new BudgetWindow.RollingHours(hours.Value),
        _ => new BudgetWindow.Monthly(),
    };
}
