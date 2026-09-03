using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="UsageExportFormatter"/>'s CSV rendering, especially RFC 4180 quoting.</summary>
public class UsageExportFormatterTests
{
    [Fact]
    public void ToCsv_NoBuckets_ReturnsHeaderOnly()
    {
        var csv = UsageExportFormatter.ToCsv([]);

        Assert.Equal(
            expected:
            "BucketStartUtc,BucketWidth,GroupKey,Requests,UnpricedRequests,PromptTokens,CompletionTokens,CacheCreationTokens,CacheReadTokens,CostUsd\r\n",
            actual: csv);
    }

    [Fact]
    public void ToCsv_OneBucket_RendersFieldsInOrder()
    {
        var bucket = new UsageRollupBucket(
            BucketStartUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, offset: TimeSpan.Zero),
            BucketWidth: "P1D",
            GroupKey: "gpt-5.4",
            3,
            1,
            100,
            50,
            0,
            10,
            1.5m);

        var csv = UsageExportFormatter.ToCsv([bucket]);
        var lines = csv.Split(separator: "\r\n", options: StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, actual: lines.Length);
        Assert.Equal(expected: "2026-01-01T00:00:00.0000000+00:00,P1D,gpt-5.4,3,1,100,50,0,10,1.5", actual: lines[1]);
    }

    [Fact]
    public void ToCsv_GroupKeyContainingComma_IsQuoted()
    {
        var bucket = new UsageRollupBucket(
            BucketStartUtc: DateTimeOffset.UnixEpoch, BucketWidth: "P1D", GroupKey: "vendor, model-x", 1, 0, 1, 1, 0, 0,
            0m);

        var csv = UsageExportFormatter.ToCsv([bucket]);

        Assert.Contains(expectedSubstring: "\"vendor, model-x\"", actualString: csv,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void ToCsv_GroupKeyContainingQuote_IsEscapedByDoubling()
    {
        var bucket = new UsageRollupBucket(
            BucketStartUtc: DateTimeOffset.UnixEpoch, BucketWidth: "P1D", GroupKey: "model \"nickname\"", 1, 0, 1, 1, 0,
            0, 0m);

        var csv = UsageExportFormatter.ToCsv([bucket]);

        Assert.Contains(expectedSubstring: "\"model \"\"nickname\"\"\"", actualString: csv,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void ToCsv_GroupKeyContainingNewline_IsQuoted()
    {
        var bucket = new UsageRollupBucket(
            BucketStartUtc: DateTimeOffset.UnixEpoch, BucketWidth: "P1D", GroupKey: "line1\nline2", 1, 0, 1, 1, 0, 0,
            0m);

        var csv = UsageExportFormatter.ToCsv([bucket]);

        Assert.Contains(expectedSubstring: "\"line1\nline2\"", actualString: csv,
            comparisonType: StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("=cmd|'/c calc'!A1")]
    [InlineData("+1+1")]
    [InlineData("-1+1")]
    [InlineData("@SUM(A1:A2)")]
    public void ToCsv_GroupKeyStartingWithFormulaTrigger_IsNeutralizedWithLeadingApostrophe(string groupKey)
    {
        var bucket = new UsageRollupBucket(
            BucketStartUtc: DateTimeOffset.UnixEpoch, BucketWidth: "P1D", GroupKey: groupKey, 1, 0, 1, 1, 0, 0, 0m);

        var csv = UsageExportFormatter.ToCsv([bucket]);

        Assert.Contains(expectedSubstring: $"'{groupKey}", actualString: csv, comparisonType: StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(" =1+1")]
    [InlineData("  +1+1")]
    [InlineData(" -1+1")]
    [InlineData(" @SUM(A1:A2)")]
    public void ToCsv_GroupKeyLeadingSpacesThenFormulaTrigger_IsNeutralizedWithLeadingApostrophe(string groupKey)
    {
        // Spreadsheet apps trim leading spaces before deciding whether a cell is a formula, so a value
        // that hides a trigger character behind leading spaces is just as dangerous as one with the
        // trigger at position 0.
        var bucket = new UsageRollupBucket(
            BucketStartUtc: DateTimeOffset.UnixEpoch, BucketWidth: "P1D", GroupKey: groupKey, 1, 0, 1, 1, 0, 0, 0m);

        var csv = UsageExportFormatter.ToCsv([bucket]);

        Assert.Contains(expectedSubstring: $"'{groupKey}", actualString: csv, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void ToCsv_GroupKeyAllSpaces_IsUnchanged()
    {
        var bucket = new UsageRollupBucket(
            BucketStartUtc: DateTimeOffset.UnixEpoch, BucketWidth: "P1D", GroupKey: "   ", 1, 0, 1, 1, 0, 0, 0m);

        var csv = UsageExportFormatter.ToCsv([bucket]);

        Assert.Contains(expectedSubstring: ",   ,", actualString: csv, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void ToCsv_GroupKeyNotStartingWithFormulaTrigger_IsUnchanged()
    {
        var bucket = new UsageRollupBucket(
            BucketStartUtc: DateTimeOffset.UnixEpoch, BucketWidth: "P1D", GroupKey: "gpt-5.4", 1, 0, 1, 1, 0, 0, 0m);

        var csv = UsageExportFormatter.ToCsv([bucket]);

        Assert.Contains(expectedSubstring: ",gpt-5.4,", actualString: csv, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void ToCsv_MultipleBuckets_OneLinePerBucketPlusHeader()
    {
        var buckets = new[]
        {
            new UsageRollupBucket(BucketStartUtc: DateTimeOffset.UnixEpoch, BucketWidth: "P1D", GroupKey: "a", 1, 0, 1,
                1, 0, 0, 0m),
            new UsageRollupBucket(BucketStartUtc: DateTimeOffset.UnixEpoch, BucketWidth: "P1D", GroupKey: "b", 2, 0, 2,
                2, 0, 0, 0m)
        };

        var csv = UsageExportFormatter.ToCsv(buckets);
        var lines = csv.Split(separator: "\r\n", options: StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, actual: lines.Length);
    }
}