using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

/// <summary>
/// Coverage for <see cref="DialectDelimiter"/>'s constructor validation (PR #86 review): an empty
/// <c>Open</c> would match at every position and make <c>Open[0]</c> throw in
/// <see cref="ToolCallDialectRegistry.ScannableOpenerFirstChars"/>; an empty <c>Close</c> would frame a
/// zero-length region instead of a real one. Both must fail at construction rather than downstream.
/// </summary>
public class DialectDelimiterTests
{
    [Fact]
    public void ValidOpenAndClose_ConstructsSuccessfully()
    {
        var delimiter = new DialectDelimiter("<tool_call>", "</tool_call>");

        Assert.Equal("<tool_call>", delimiter.Open);
        Assert.Equal("</tool_call>", delimiter.Close);
    }

    [Fact]
    public void NullClose_IsAllowed_AndMeansRunsToEndOfMessage()
    {
        var delimiter = new DialectDelimiter("[TOOL_CALLS]", Close: null);

        Assert.Null(delimiter.Close);
    }

    [Fact]
    public void EmptyOpen_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => new DialectDelimiter(string.Empty, "</x>"));
        Assert.Equal("Open", ex.ParamName);
    }

    [Fact]
    public void EmptyClose_Throws()
    {
        // Distinct from null: null is the valid "runs to end of message" sentinel, so only an empty
        // *string* close - which would frame a zero-length region - is rejected.
        var ex = Assert.Throws<ArgumentException>(() => new DialectDelimiter("<x>", string.Empty));
        Assert.Equal("Close", ex.ParamName);
    }
}

