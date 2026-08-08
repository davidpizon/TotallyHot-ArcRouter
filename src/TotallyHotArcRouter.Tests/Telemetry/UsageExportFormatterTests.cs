using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="UsageExportFormatter"/>'s CSV rendering, especially RFC 4180 quoting.</summary>
public class UsageExportFormatterTests
{
    [Fact]
    public void ToCsv_NoBuckets_ReturnsHeaderOnly()
    {
        var csv = UsageExportFormatter.ToCsv([]);

        Assert.Equal("BucketStartUtc,BucketWidth,GroupKey,Requests,UnpricedRequests,PromptTokens,CompletionTokens,CacheCreationTokens,CacheReadTokens,CostUsd\r\n", csv);
    }

    [Fact]
    public void ToCsv_OneBucket_RendersFieldsInOrder()
    {
        var bucket = new UsageRollupBucket(
            BucketStartUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            BucketWidth: "P1D",
            GroupKey: "gpt-5.4",
            Requests: 3,
            UnpricedRequests: 1,
            PromptTokens: 100,
            CompletionTokens: 50,
            CacheCreationTokens: 0,
            CacheReadTokens: 10,
            CostUsd: 1.5m);

        var csv = UsageExportFormatter.ToCsv([bucket]);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Equal("2026-01-01T00:00:00.0000000+00:00,P1D,gpt-5.4,3,1,100,50,0,10,1.5", lines[1]);
    }

    [Fact]
    public void ToCsv_GroupKeyContainingComma_IsQuoted()
    {
        var bucket = new UsageRollupBucket(
            DateTimeOffset.UnixEpoch, "P1D", "vendor, model-x", 1, 0, 1, 1, 0, 0, 0m);

        var csv = UsageExportFormatter.ToCsv([bucket]);

        Assert.Contains("\"vendor, model-x\"", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void ToCsv_GroupKeyContainingQuote_IsEscapedByDoubling()
    {
        var bucket = new UsageRollupBucket(
            DateTimeOffset.UnixEpoch, "P1D", "model \"nickname\"", 1, 0, 1, 1, 0, 0, 0m);

        var csv = UsageExportFormatter.ToCsv([bucket]);

        Assert.Contains("\"model \"\"nickname\"\"\"", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void ToCsv_GroupKeyContainingNewline_IsQuoted()
    {
        var bucket = new UsageRollupBucket(
            DateTimeOffset.UnixEpoch, "P1D", "line1\nline2", 1, 0, 1, 1, 0, 0, 0m);

        var csv = UsageExportFormatter.ToCsv([bucket]);

        Assert.Contains("\"line1\nline2\"", csv, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("=cmd|'/c calc'!A1")]
    [InlineData("+1+1")]
    [InlineData("-1+1")]
    [InlineData("@SUM(A1:A2)")]
    public void ToCsv_GroupKeyStartingWithFormulaTrigger_IsNeutralizedWithLeadingApostrophe(string groupKey)
    {
        var bucket = new UsageRollupBucket(
            DateTimeOffset.UnixEpoch, "P1D", groupKey, 1, 0, 1, 1, 0, 0, 0m);

        var csv = UsageExportFormatter.ToCsv([bucket]);

        Assert.Contains($"'{groupKey}", csv, StringComparison.Ordinal);
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
            DateTimeOffset.UnixEpoch, "P1D", groupKey, 1, 0, 1, 1, 0, 0, 0m);

        var csv = UsageExportFormatter.ToCsv([bucket]);

        Assert.Contains($"'{groupKey}", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void ToCsv_GroupKeyAllSpaces_IsUnchanged()
    {
        var bucket = new UsageRollupBucket(
            DateTimeOffset.UnixEpoch, "P1D", "   ", 1, 0, 1, 1, 0, 0, 0m);

        var csv = UsageExportFormatter.ToCsv([bucket]);

        Assert.Contains(",   ,", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void ToCsv_GroupKeyNotStartingWithFormulaTrigger_IsUnchanged()
    {
        var bucket = new UsageRollupBucket(
            DateTimeOffset.UnixEpoch, "P1D", "gpt-5.4", 1, 0, 1, 1, 0, 0, 0m);

        var csv = UsageExportFormatter.ToCsv([bucket]);

        Assert.Contains(",gpt-5.4,", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void ToCsv_MultipleBuckets_OneLinePerBucketPlusHeader()
    {
        var buckets = new[]
        {
            new UsageRollupBucket(DateTimeOffset.UnixEpoch, "P1D", "a", 1, 0, 1, 1, 0, 0, 0m),
            new UsageRollupBucket(DateTimeOffset.UnixEpoch, "P1D", "b", 2, 0, 2, 2, 0, 0, 0m),
        };

        var csv = UsageExportFormatter.ToCsv(buckets);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
    }
}
