using System.Text;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Pins <see cref="LogRedaction"/>'s truncate-then-sanitize helpers to the result the old
/// sanitize-then-truncate order produced, so bounding the work did not change what is logged.
/// </summary>
public sealed class LogRedactionTests
{
    /// <summary>A long body with line breaks logs identically to the original sanitize-then-truncate order.</summary>
    [Fact]
    public void TruncateSanitize_MatchesSanitizeThenTruncate()
    {
        var body = string.Concat(Enumerable.Repeat(element: "line\r\nof text ", count: 2000));

        Assert.Equal(
            expected: LogRedaction.Truncate(LogRedaction.Sanitize(body)),
            actual: LogRedaction.TruncateSanitize(body));
    }

    /// <summary>Decoding only a byte prefix of a large multibyte payload still yields the same log text.</summary>
    [Fact]
    public void DecodeTruncateSanitize_LargeMultibytePayload_MatchesFullDecode()
    {
        var body = string.Concat(Enumerable.Repeat(element: "héllo\n世界 ", count: 5000));
        var bytes = Encoding.UTF8.GetBytes(body);

        Assert.Equal(
            expected: LogRedaction.Truncate(LogRedaction.Sanitize(body)),
            actual: LogRedaction.DecodeTruncateSanitize(bytes));
    }

    /// <summary>A small payload passes through with only CR/LF replaced and no truncation marker.</summary>
    [Fact]
    public void DecodeTruncateSanitize_SmallPayload_IsSanitizedNotTruncated()
    {
        Assert.Equal(expected: "a b c", actual: LogRedaction.DecodeTruncateSanitize("a\nb\rc"u8));
    }
}
